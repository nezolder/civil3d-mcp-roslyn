---
name: corridor_section_check
category: corridors
description: Check the built corridor sections of one corridor at its own or at sample line stations for repeated link codes on one side, overlapping links, missing shape codes and too short links, reported by station, side, offset and code
requires_write: false
aliases: ["korridor keresztszelvények ellenőrzése dupla szegély átfedő link hiányzó réteg", "corridor section check duplicate codes overlapping links missing shapes", "nyomterv szelvényellenőrző rövid linkek", "corridor cross section quality check by station"]
workflow_tags: ["audit"]
tested_civil_version: "2025"
validation_summary: "Offline: compiles against the Civil 3D 2025 assemblies. Not yet run in Civil 3D."
parameters:
  - name: corridorHandle
    type: string
    required: true
    description: Hex handle of one corridor
  - name: baselineNameFilter
    type: string
    required: false
    description: Exact baseline name; empty checks every baseline (default "")
  - name: sampleLineGroupHandle
    type: string
    required: false
    description: Hex handle of a sample line group; when given, only its stations are checked, on baselines that follow its alignment (default "")
  - name: startStation
    type: double
    required: false
    description: First station to check; NaN means the baseline start (default NaN)
  - name: endStation
    type: double
    required: false
    description: Last station to check; NaN means the baseline end (default NaN)
  - name: uniqueLinkCodes
    type: array
    required: false
    description: Link codes that must form one connected run of links on each side of a section, for example a curb's code (default none)
  - name: overlapLinkCodes
    type: array
    required: false
    description: Link codes whose links must not overlap across the offset, within one subassembly or between subassemblies (default Top)
  - name: requiredShapeCodes
    type: array
    required: false
    description: Shape codes that every checked section must contain, for example the pavement layers (default none)
  - name: shortLinkCodes
    type: array
    required: false
    description: Link codes checked for a minimum length; empty checks every coded link (default none)
  - name: minLinkLength
    type: double
    required: false
    description: Links of shortLinkCodes shorter than this, in drawing units, are reported; 0 switches the check off (default 0.30)
  - name: overlapTolerance
    type: double
    required: false
    description: Offset overlap smaller than this is ignored, in drawing units (default 0.01)
  - name: maxStations
    type: int
    required: false
    description: Maximum stations checked over all baselines, 1 to 2000 (default 500)
  - name: limit
    type: int
    required: false
    description: Maximum findings listed, 1 to 500 (default 200)
---

## Code Template

```csharp
var corridorHandle = "CORRIDOR_HANDLE";
var baselineNameFilter = "";
var sampleLineGroupHandle = "";
var startStation = double.NaN;
var endStation = double.NaN;
var uniqueLinkCodes = new string[] { };
var overlapLinkCodes = new string[] { "Top" };
var requiredShapeCodes = new string[] { };
var shortLinkCodes = new string[] { };
var minLinkLength = 0.30;
var overlapTolerance = 0.01;
var maxStations = 500;
var limit = 200;

uniqueLinkCodes ??= new string[] { };
overlapLinkCodes ??= new string[] { };
requiredShapeCodes ??= new string[] { };
shortLinkCodes ??= new string[] { };
if (maxStations < 1 || maxStations > 2000) return new { success = false, error = "maxStations must be between 1 and 2000." };
if (limit < 1 || limit > 500) return new { success = false, error = "limit must be between 1 and 500." };
if (!double.IsFinite(minLinkLength) || minLinkLength < 0 || !double.IsFinite(overlapTolerance) || overlapTolerance < 0)
    return new { success = false, error = "minLinkLength and overlapTolerance must be finite and not negative." };
if (!double.IsNaN(startStation) && !double.IsNaN(endStation) && endStation < startStation)
    return new { success = false, error = "endStation must not be before startStation." };

ObjectId Resolve(string text)
{
    if (string.IsNullOrWhiteSpace(text)) return ObjectId.Null;
    if (!long.TryParse(text.Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
        return ObjectId.Null;
    return Database.TryGetObjectId(new Handle(value), out var id) && !id.IsErased ? id : ObjectId.Null;
}

var corridorId = Resolve(corridorHandle);
var corridor = corridorId.IsNull ? null : Transaction.GetObject(corridorId, OpenMode.ForRead) as Corridor;
if (corridor == null) return new { success = false, error = "corridorHandle does not resolve to a corridor in this drawing." };

List<double> sampleStations = null;
ObjectId sampleAlignmentId = ObjectId.Null;
if (!string.IsNullOrWhiteSpace(sampleLineGroupHandle))
{
    var groupId = Resolve(sampleLineGroupHandle);
    var group = groupId.IsNull ? null : Transaction.GetObject(groupId, OpenMode.ForRead) as SampleLineGroup;
    if (group == null) return new { success = false, error = "sampleLineGroupHandle does not resolve to a sample line group." };
    sampleAlignmentId = group.ParentAlignmentId;
    sampleStations = group.GetSampleLineIds().Cast<ObjectId>()
        .Select(id => Transaction.GetObject(id, OpenMode.ForRead) as SampleLine)
        .Where(line => line != null)
        .Select(line => line.Station)
        .OrderBy(s => s)
        .ToList();
}

bool Has(string[] codes, CorridorCodeCollection actual)
{
    for (var i = 0; i < actual.Count; i++)
        if (codes.Any(c => string.Equals(c, actual[i], StringComparison.OrdinalIgnoreCase))) return true;
    return false;
}
List<string> CodesOf(CorridorCodeCollection actual)
{
    var list = new List<string>();
    for (var i = 0; i < actual.Count; i++) list.Add(actual[i]);
    return list;
}

var findings = new List<Dictionary<string, object>>();
var findingCounts = new Dictionary<string, int>();
void Report(string kind, string baseline, double station, string side, double offset, string code, string subassembly, object detail)
{
    findingCounts[kind] = findingCounts.TryGetValue(kind, out var n) ? n + 1 : 1;
    if (findings.Count >= limit) return;
    findings.Add(new Dictionary<string, object>
    {
        ["finding"] = kind, ["baseline"] = baseline, ["station"] = Math.Round(station, 4), ["side"] = side,
        ["offset"] = Math.Round(offset, 4), ["code"] = code, ["subassembly"] = subassembly, ["detail"] = detail
    });
}

var subassemblyNames = new Dictionary<ObjectId, string>();
string SubassemblyName(ObjectId id)
{
    if (id.IsNull) return null;
    if (subassemblyNames.TryGetValue(id, out var name)) return name;
    try { name = (Transaction.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Subassembly)?.Name; } catch (System.Exception) { name = null; }
    subassemblyNames[id] = name;
    return name;
}

var codesSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var checkedStations = 0;
var notCorridorStations = new List<object>();
var notCorridorStationCount = 0;
var stationsWithoutAssembly = 0;
var truncated = false;
var baselineRows = new List<object>();
foreach (Baseline baseline in corridor.Baselines)
{
    if (!string.IsNullOrWhiteSpace(baselineNameFilter) && !string.Equals(baseline.Name, baselineNameFilter.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
    if (sampleStations != null && baseline.AlignmentId != sampleAlignmentId)
    {
        baselineRows.Add(new { baseline = baseline.Name, skipped = "does not follow the sample line group's alignment" });
        continue;
    }
    var from = double.IsNaN(startStation) ? baseline.StartStation : Math.Max(startStation, baseline.StartStation);
    var to = double.IsNaN(endStation) ? baseline.EndStation : Math.Min(endStation, baseline.EndStation);
    var stations = (sampleStations ?? baseline.SortedStations().ToList()).Where(s => s >= from - 1e-6 && s <= to + 1e-6).ToList();
    var checkedHere = 0;
    foreach (var station in stations)
    {
        if (checkedStations >= maxStations) { truncated = true; break; }
        checkedStations++;
        checkedHere++;
        AppliedAssembly applied;
        try { applied = baseline.GetAppliedAssemblyAtStation(station); }
        catch (System.ArgumentException)
        {
            // "Station not processed": not a station of the built corridor, so no section was built there.
            applied = null;
            if (notCorridorStations.Count < limit) notCorridorStations.Add(new { baseline = baseline.Name, station = Math.Round(station, 4) });
            notCorridorStationCount++;
            continue;
        }
        catch (System.Exception) { applied = null; }
        if (applied == null) { stationsWithoutAssembly++; continue; }

        // Every link with its offset range and the subassembly it belongs to.
        var links = new List<(string sub, ObjectId subId, List<string> codes, double minOffset, double maxOffset, double length, double midOffset, Point2d first, Point2d last)>();
        foreach (AppliedSubassembly sub in applied.GetAppliedSubassemblies())
        {
            foreach (CalculatedLink link in sub.Links)
            {
                var pts = new List<Point3d>();
                foreach (CalculatedPoint p in link.CalculatedPoints) pts.Add(p.StationOffsetElevationToBaseline);
                if (pts.Count < 2) continue;
                double length = 0;
                for (var i = 1; i < pts.Count; i++)
                    length += Math.Sqrt(Math.Pow(pts[i].Y - pts[i - 1].Y, 2) + Math.Pow(pts[i].Z - pts[i - 1].Z, 2));
                var offsets = pts.Select(p => p.Y).ToList();
                var codes = CodesOf(link.CorridorCodes);
                foreach (var c in codes) codesSeen[c] = codesSeen.TryGetValue(c, out var seen) ? seen + 1 : 1;
                links.Add((SubassemblyName(sub.SubassemblyId), sub.SubassemblyId, codes, offsets.Min(), offsets.Max(), length, (offsets.Min() + offsets.Max()) / 2,
                    new Point2d(pts[0].Y, pts[0].Z), new Point2d(pts[pts.Count - 1].Y, pts[pts.Count - 1].Z)));
            }
        }
        string Side(double offset) => offset < 0 ? "left" : "right";

        // A code given as unique must form one connected run of links per side; two separate runs
        // (for example a second curb) are reported, whether they come from one subassembly or several.
        foreach (var code in uniqueLinkCodes.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var coded = links.Where(l => l.codes.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase))).ToList();
            var parent = Enumerable.Range(0, coded.Count).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            bool Touch(Point2d a, Point2d b) => a.GetDistanceTo(b) <= Math.Max(overlapTolerance, 1e-4);
            for (var i = 0; i < coded.Count; i++)
                for (var j = i + 1; j < coded.Count; j++)
                    if (Touch(coded[i].first, coded[j].first) || Touch(coded[i].first, coded[j].last) || Touch(coded[i].last, coded[j].first) || Touch(coded[i].last, coded[j].last))
                        parent[Find(i)] = Find(j);
            var runs = Enumerable.Range(0, coded.Count).GroupBy(Find).Select(g => g.Select(i => coded[i]).ToList()).ToList();
            foreach (var side in runs.GroupBy(r => Side((r.Min(l => l.minOffset) + r.Max(l => l.maxOffset)) / 2)).Where(g => g.Count() > 1))
            {
                var ordered = side.OrderBy(r => r.Min(l => Math.Abs(l.midOffset))).ToList();
                Report("REPEATED_CODE_ON_ONE_SIDE", baseline.Name, station, side.Key, ordered[1].Min(l => l.minOffset), code,
                    string.Join(", ", side.SelectMany(r => r.Select(l => l.sub)).Distinct()),
                    new { runs = side.Count(), offsets = ordered.Select(r => new[] { Math.Round(r.Min(l => l.minOffset), 3), Math.Round(r.Max(l => l.maxOffset), 3) }).ToList() });
            }
        }

        // Links with the same code whose offset ranges overlap by more than the tolerance, within one subassembly or across.
        foreach (var code in overlapLinkCodes.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var coded = links.Where(l => l.codes.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase))).OrderBy(l => l.minOffset).ToList();
            for (var i = 0; i < coded.Count; i++)
                for (var j = i + 1; j < coded.Count; j++)
                {
                    var overlap = Math.Min(coded[i].maxOffset, coded[j].maxOffset) - Math.Max(coded[i].minOffset, coded[j].minOffset);
                    if (overlap > overlapTolerance)
                        Report("OVERLAPPING_LINKS", baseline.Name, station, Side(coded[j].minOffset + overlap / 2), coded[j].minOffset, code,
                            coded[i].subId == coded[j].subId ? coded[i].sub : coded[i].sub + " / " + coded[j].sub,
                            new { overlap = Math.Round(overlap, 4), sameSubassembly = coded[i].subId == coded[j].subId });
                }
        }

        if (requiredShapeCodes.Length > 0)
        {
            var shapes = applied.Shapes;
            foreach (var code in requiredShapeCodes.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                var found = false;
                foreach (CalculatedShape shape in shapes)
                    if (Has(new[] { code }, shape.CorridorCodes)) { found = true; break; }
                if (!found) Report("MISSING_SHAPE_CODE", baseline.Name, station, null, 0, code, null, null);
            }
        }

        if (minLinkLength > 0)
        {
            foreach (var l in links.Where(l => l.codes.Count > 0 && l.length < minLinkLength
                && (shortLinkCodes.Length == 0 || l.codes.Any(c => shortLinkCodes.Any(s => string.Equals(s, c, StringComparison.OrdinalIgnoreCase))))))
                Report("SHORT_LINK", baseline.Name, station, Side(l.midOffset), l.midOffset, string.Join(",", l.codes), l.sub, new { length = Math.Round(l.length, 4) });
        }
    }
    baselineRows.Add(new { baseline = baseline.Name, from = Math.Round(from, 4), to = Math.Round(to, 4), stationsChecked = checkedHere });
    if (truncated) break;
}

return new
{
    success = true,
    corridor = corridor.Name,
    corridorOutOfDate = corridor.IsOutOfDate,
    stationSource = sampleStations != null ? "sampleLineGroup" : "corridorStations",
    baselines = baselineRows,
    stationsChecked = checkedStations,
    stationsWithoutAssembly,
    // Sample line stations where the corridor has no station: nothing was checked there.
    notCorridorStationCount,
    notCorridorStations,
    stationLimitReached = truncated,
    findingCounts,
    // Codes given in a parameter that no checked link carries: such a check found nothing because it had nothing to look at.
    codesNotSeen = uniqueLinkCodes.Concat(overlapLinkCodes).Concat(shortLinkCodes).Where(c => !string.IsNullOrWhiteSpace(c) && !codesSeen.ContainsKey(c))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
    linkCodesSeen = codesSeen.OrderByDescending(p => p.Value).Take(40).ToDictionary(p => p.Key, p => p.Value),
    findingsListed = findings.Count,
    findings
};
```

## Usage Notes

- Read-only: it reads the corridor as it was last built and changes nothing. With `corridorOutOfDate` true the result describes the old build; ask the user to rebuild first.
- Stations: by default the corridor's own stations of each matching baseline; with `sampleLineGroupHandle` exactly the sample line stations, on baselines that follow the group's alignment, which is what the section sheets will show. Offset baselines are not checked.
- Checks, each switched on by its input: `uniqueLinkCodes` (links with a code, such as a curb's, that form two or more separate runs on the same side; links sharing an end point are one run: `REPEATED_CODE_ON_ONE_SIDE`), `overlapLinkCodes` (links with the code covering the same offsets, also inside one subassembly; `sameSubassembly` in the detail tells which: `OVERLAPPING_LINKS`), `requiredShapeCodes` (`MISSING_SHAPE_CODE` at a station without such a shape), `minLinkLength` with `shortLinkCodes` (`SHORT_LINK`; a short link often gives an unreadable label).
- A sample line station that is not a station of the built corridor has no section to check (Civil 3D answers "Station not processed"); it is counted in `notCorridorStationCount` and listed in `notCorridorStations`, not checked. Seen live: 23 of 30 junction sample lines. `add_sample_line_stations_to_corridor` adds such stations; rebuild the corridor and check again.
- Side is by offset sign: negative is left. A link is placed by the middle of its offset range; length is measured along its points in the section plane (offset and elevation).
- Codes and lengths are the caller's: the recipe holds no office or standard values, and the default `Top` is only the stock subassemblies' code. `linkCodesSeen` lists the codes of the checked links with counts and `codesNotSeen` the given codes that no link carries; a check whose code is not seen found nothing because it had nothing to look at, so run once, read `linkCodesSeen` and run again with the drawing's own codes. Which findings matter is an engineering decision; the result lists where to look.
- One assembly built from a single Subassembly Composer subassembly with all links (seen live: 8 to 42 links per station) is checked the same way as one built from several stock subassemblies.
- Found in field use (Civil 3D 2025): duplicated curbs, overlapping links, missing layers and short labelled links had to be found by ad hoc scripts on every review round.
