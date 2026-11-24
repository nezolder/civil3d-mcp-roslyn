---
name: create_breaklines_from_survey_strings
category: surfaces
description: Join surveyed points that run along the road (kerb, edge, ditch lines) into 3D polylines, grouped by layer or by a point code, skipping ties that are too long or jump sideways, and optionally add them to a TIN surface as one separate breakline group
requires_write: true
aliases: ["terep töréspontvonalak felmért hosszirányú pontsorokból", "survey point strings to 3D polylines and surface breaklines", "geodéziai pontok összekötése 3D vonallá pontkód szerint", "breaklines from surveyed longitudinal point strings"]
workflow_tags: ["modeling", "source_state"]
tested_civil_version: "2025"
validation_summary: "Offline: compiles against the Civil 3D 2025 assemblies. Not yet run in Civil 3D."
parameters:
  - name: alignmentHandle
    type: string
    required: true
    description: Hex handle of the alignment the strings run along; points are ordered by its stations
  - name: includePoints
    type: bool
    required: false
    description: Read AutoCAD point objects (POINT) (default true)
  - name: includeBlocks
    type: bool
    required: false
    description: Read block references at their insertion points (default true)
  - name: includeXrefs
    type: bool
    required: false
    description: Also read points and blocks inside attached, loaded xrefs (default true)
  - name: includeCogoPoints
    type: bool
    required: false
    description: Also read COGO points of this drawing, with the raw description as their code (default true)
  - name: layerFilter
    type: string
    required: false
    description: Layer patterns separated by "|", with * and ? wildcards; xref layers also match without their "xref|" prefix; empty keeps every layer (default "")
  - name: blockNames
    type: string
    required: false
    description: Block name patterns separated by "|"; empty keeps every block (default "")
  - name: codeAttributeTag
    type: string
    required: false
    description: Tag of the block attribute holding the surveyor's point code; when given, points are grouped by that code, otherwise by layer (default "")
  - name: codeFilter
    type: string
    required: false
    description: Code (or layer) patterns separated by "|" that form strings, for example kerb and edge codes; empty uses every group (default "")
  - name: elevationAttributeTag
    type: string
    required: false
    description: Tag of the block attribute that holds the height, for surveys drawn flat; empty uses the geometry's Z (default "")
  - name: maxTieLength
    type: double
    required: false
    description: Two neighbouring points are joined only when they are at most this far apart in plan (default 30)
  - name: maxLateralJump
    type: double
    required: false
    description: Two neighbouring points are joined only when their offsets from the alignment differ by at most this much (default 0.8)
  - name: maxOffset
    type: double
    required: false
    description: Points farther than this from the alignment are ignored (default 50)
  - name: minPointsPerString
    type: int
    required: false
    description: Strings with fewer points are dropped, at least 2 (default 3)
  - name: polylineLayer
    type: string
    required: true
    description: Layer for the new 3D polylines; created with default properties when missing
  - name: surfaceHandle
    type: string
    required: false
    description: Hex handle of a TIN surface that gets the polylines as one standard breakline group; empty only draws the polylines (default "")
  - name: breaklineDescription
    type: string
    required: false
    description: Description of the new breakline group (default "Survey strings")
  - name: scanLimit
    type: int
    required: false
    description: Maximum objects read, 1 to 500000 (default 200000)
  - name: maxStrings
    type: int
    required: false
    description: Refuse when more strings would be drawn, 1 to 2000 (default 500)
---

## Code Template

```csharp
var alignmentHandle = "ALIGNMENT_HANDLE";
var includePoints = true;
var includeBlocks = true;
var includeXrefs = true;
var includeCogoPoints = true;
var layerFilter = "";
var blockNames = "";
var codeAttributeTag = "";
var codeFilter = "";
var elevationAttributeTag = "";
var maxTieLength = 30.0;
var maxLateralJump = 0.8;
var maxOffset = 50.0;
var minPointsPerString = 3;
var polylineLayer = "POLYLINE_LAYER";
var surfaceHandle = "";
var breaklineDescription = "Survey strings";
var scanLimit = 200000;
var maxStrings = 500;

if (!double.IsFinite(maxTieLength) || maxTieLength <= 0 || !double.IsFinite(maxLateralJump) || maxLateralJump < 0 || !double.IsFinite(maxOffset) || maxOffset <= 0)
    return new { success = false, error = "maxTieLength and maxOffset must be greater than 0, maxLateralJump not negative." };
if (minPointsPerString < 2) return new { success = false, error = "minPointsPerString must be at least 2." };
if (scanLimit < 1 || scanLimit > 500000 || maxStrings < 1 || maxStrings > 2000)
    return new { success = false, error = "scanLimit must be 1-500000 and maxStrings 1-2000." };
polylineLayer = (polylineLayer ?? "").Trim();
if (polylineLayer.Length == 0 || polylineLayer == "POLYLINE_LAYER")
    return new { success = false, error = "Name the layer for the new 3D polylines (polylineLayer)." };
try { SymbolUtilityServices.ValidateSymbolName(polylineLayer, false); }
catch (System.Exception) { return new { success = false, error = "polylineLayer is not a valid layer name." }; }
codeAttributeTag = (codeAttributeTag ?? "").Trim();
elevationAttributeTag = (elevationAttributeTag ?? "").Trim();

List<string> SplitList(string text) => (text ?? "").Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
System.Text.RegularExpressions.Regex ToPattern(string pattern) => new System.Text.RegularExpressions.Regex(
    "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
var layerPatterns = SplitList(layerFilter).Select(ToPattern).ToList();
var blockPatterns = SplitList(blockNames).Select(ToPattern).ToList();
var codePatterns = SplitList(codeFilter).Select(ToPattern).ToList();
bool MatchesAny(List<System.Text.RegularExpressions.Regex> patterns, string name)
{
    var text = (name ?? "").Trim();
    var shortName = text.Contains('|') ? text.Substring(text.LastIndexOf('|') + 1) : text;
    return patterns.Count == 0 || patterns.Any(p => p.IsMatch(text) || p.IsMatch(shortName));
}
double? ParseNumber(string text)
{
    var value = (text ?? "").Trim().Replace(',', '.');
    return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : (double?)null;
}
ObjectId Resolve(string text)
{
    if (string.IsNullOrWhiteSpace(text)) return ObjectId.Null;
    if (!long.TryParse(text.Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
        return ObjectId.Null;
    return Database.TryGetObjectId(new Handle(value), out var id) && !id.IsErased ? id : ObjectId.Null;
}

// Host access starts here.
var alignmentId = Resolve(alignmentHandle);
var alignment = alignmentId.IsNull ? null : Transaction.GetObject(alignmentId, OpenMode.ForRead) as Alignment;
if (alignment == null) return new { success = false, error = "alignmentHandle does not resolve to an alignment." };
TinSurface surface = null;
if (!string.IsNullOrWhiteSpace(surfaceHandle))
{
    var surfaceId = Resolve(surfaceHandle);
    surface = surfaceId.IsNull ? null : Transaction.GetObject(surfaceId, OpenMode.ForRead) as TinSurface;
    if (surface == null) return new { success = false, error = "surfaceHandle does not resolve to a TIN surface." };
    if (surface.IsReferenceObject) return new { success = false, error = "The surface is a data reference; add breaklines in its source drawing." };
}

// Survey points with a height and a group key (code or layer).
var points = new List<(double x, double y, double z, string key, string source)>();
var seen = new HashSet<(long, long)>();
int scanned = 0, withoutHeight = 0, filteredOut = 0;
var truncated = false;
var xrefsRead = new List<string>();
void Collect(double x, double y, double? z, string key, string source)
{
    if (!z.HasValue) { withoutHeight++; return; }
    if (!MatchesAny(codePatterns, key)) { filteredOut++; return; }
    if (!seen.Add(((long)Math.Round(x / 1e-3), (long)Math.Round(y / 1e-3)))) return;
    points.Add((x, y, z.Value, key, source));
}
void Walk(ObjectId ownerId, Matrix3d transform, int depth, string xrefName)
{
    var owner = (BlockTableRecord)Transaction.GetObject(ownerId, OpenMode.ForRead);
    foreach (ObjectId id in owner)
    {
        if (scanned >= scanLimit) { truncated = true; return; }
        scanned++;
        var entity = Transaction.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
        if (entity == null || entity.IsErased) continue;
        var source = xrefName == null ? entity.Handle.ToString() : xrefName + ":" + entity.Handle.ToString();
        if (entity is DBPoint point)
        {
            if (!includePoints || codeAttributeTag.Length > 0 || !MatchesAny(layerPatterns, entity.Layer)) { filteredOut++; continue; }
            var p = point.Position.TransformBy(transform);
            Collect(p.X, p.Y, elevationAttributeTag.Length == 0 ? p.Z : (double?)null, entity.Layer, source);
        }
        else if (entity is BlockReference block)
        {
            var record = (BlockTableRecord)Transaction.GetObject(block.BlockTableRecord, OpenMode.ForRead);
            if (record.IsFromExternalReference || record.IsFromOverlayReference)
            {
                if (!includeXrefs || !record.IsResolved || depth >= 4) continue;
                if (!xrefsRead.Contains(record.Name)) xrefsRead.Add(record.Name);
                Walk(record.ObjectId, transform * block.BlockTransform, depth + 1, record.Name);
                continue;
            }
            if (!includeBlocks || record.IsLayout) continue;
            var blockName = record.Name;
            if (block.IsDynamicBlock && !block.DynamicBlockTableRecord.IsNull)
                blockName = ((BlockTableRecord)Transaction.GetObject(block.DynamicBlockTableRecord, OpenMode.ForRead)).Name;
            if (!MatchesAny(blockPatterns, blockName) || !MatchesAny(layerPatterns, entity.Layer)) { filteredOut++; continue; }
            string code = null, heightText = null;
            foreach (ObjectId attributeId in block.AttributeCollection)
            {
                if (!(Transaction.GetObject(attributeId, OpenMode.ForRead) is AttributeReference attribute) || attribute.IsErased) continue;
                if (codeAttributeTag.Length > 0 && string.Equals(attribute.Tag, codeAttributeTag, StringComparison.OrdinalIgnoreCase)) code = attribute.TextString?.Trim();
                if (elevationAttributeTag.Length > 0 && string.Equals(attribute.Tag, elevationAttributeTag, StringComparison.OrdinalIgnoreCase)) heightText = attribute.TextString;
            }
            if (codeAttributeTag.Length > 0 && string.IsNullOrEmpty(code)) { filteredOut++; continue; }
            var p = block.Position.TransformBy(transform);
            Collect(p.X, p.Y, elevationAttributeTag.Length == 0 ? p.Z : ParseNumber(heightText), codeAttributeTag.Length > 0 ? code : entity.Layer, source);
        }
    }
}
if (includePoints || includeBlocks) Walk(SymbolUtilityServices.GetBlockModelSpaceId(Database), Matrix3d.Identity, 0, null);
if (includeCogoPoints)
{
    foreach (ObjectId id in CivilDoc.CogoPoints)
    {
        if (scanned >= scanLimit) { truncated = true; break; }
        scanned++;
        var cogo = Transaction.GetObject(id, OpenMode.ForRead) as CogoPoint;
        if (cogo == null || cogo.IsErased || !MatchesAny(layerPatterns, cogo.Layer)) continue;
        var key = codeAttributeTag.Length > 0 ? (cogo.RawDescription ?? "").Trim() : cogo.Layer;
        Collect(cogo.Easting, cogo.Northing, cogo.Elevation, key, "#" + cogo.PointNumber);
    }
}

// Each group, in station order: a point continues the open string whose last point is closest in offset,
// when the tie is short enough and does not jump sideways; otherwise it starts a new string.
var strings = new List<(string key, List<(double station, double offset, Point3d point)> items)>();
int outside = 0, tooFar = 0, longTies = 0, lateralJumps = 0;
foreach (var group in points.GroupBy(p => p.key, StringComparer.OrdinalIgnoreCase))
{
    var projected = new List<(double station, double offset, Point3d point)>();
    foreach (var p in group)
    {
        double station = 0, offset = 0;
        try { alignment.StationOffset(p.x, p.y, ref station, ref offset); }
        catch (System.Exception) { outside++; continue; }
        if (Math.Abs(offset) > maxOffset) { tooFar++; continue; }
        projected.Add((station, offset, new Point3d(p.x, p.y, p.z)));
    }
    var open = new List<List<(double station, double offset, Point3d point)>>();
    foreach (var item in projected.OrderBy(i => i.station))
    {
        List<(double station, double offset, Point3d point)> best = null;
        var bestJump = double.MaxValue;
        var sawLong = false; var sawJump = false;
        foreach (var candidate in open)
        {
            var last = candidate[candidate.Count - 1];
            var tie = Math.Sqrt(Math.Pow(item.point.X - last.point.X, 2) + Math.Pow(item.point.Y - last.point.Y, 2));
            var jump = Math.Abs(item.offset - last.offset);
            if (tie > maxTieLength) { sawLong = true; continue; }
            if (jump > maxLateralJump) { sawJump = true; continue; }
            if (jump < bestJump) { bestJump = jump; best = candidate; }
        }
        if (best != null) { best.Add(item); continue; }
        if (open.Count > 0) { if (sawLong) longTies++; else if (sawJump) lateralJumps++; }
        var fresh = new List<(double station, double offset, Point3d point)> { item };
        open.Add(fresh);
        strings.Add((group.Key, fresh));
    }
}
var kept = strings.Where(s => s.items.Count >= minPointsPerString).ToList();
if (kept.Count > maxStrings)
    return new { success = false, error = $"{kept.Count} strings would be drawn, more than maxStrings {maxStrings}; narrow the codes or layers." };
if (kept.Count == 0)
    return new { success = false, error = "No string with enough points was found.", pointsRead = points.Count, withoutHeight, filteredOut, scanTruncated = truncated };

// Write: layer, 3D polylines, then one breakline group.
var layers = (LayerTable)Transaction.GetObject(Database.LayerTableId, OpenMode.ForRead);
var layerCreated = false;
if (!layers.Has(polylineLayer))
{
    layers.UpgradeOpen();
    var record = new LayerTableRecord { Name = polylineLayer };
    layers.Add(record);
    Transaction.AddNewlyCreatedDBObject(record, true);
    layerCreated = true;
}
var model = (BlockTableRecord)Transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(Database), OpenMode.ForWrite);
var created = new ObjectIdCollection();
var rows = new List<object>();
foreach (var s in kept)
{
    var collection = new Point3dCollection(s.items.Select(i => i.point).ToArray());
    var polyline = new Polyline3d(Poly3dType.SimplePoly, collection, false) { Layer = polylineLayer };
    model.AppendEntity(polyline);
    Transaction.AddNewlyCreatedDBObject(polyline, true);
    created.Add(polyline.ObjectId);
    if (rows.Count < 200)
        rows.Add(new
        {
            handle = polyline.Handle.ToString(), code = s.key, points = s.items.Count,
            fromStation = Math.Round(s.items[0].station, 3), toStation = Math.Round(s.items[s.items.Count - 1].station, 3),
            offset = Math.Round(s.items.Average(i => i.offset), 3), length = Math.Round(polyline.Length, 3)
        });
}
object breaklines = null;
if (surface != null)
{
    surface.UpgradeOpen();
    var operationsBefore = surface.Operations.Count;
    var operation = surface.BreaklinesDefinition.AddStandardBreaklines(created, 0.0, 0.0, 0.0, 0.0);
    operation.Description = breaklineDescription ?? "";
    if (surface.Operations.Count != operationsBefore + 1)
        throw new InvalidOperationException("The surface did not record exactly one new breakline operation.");
    breaklines = new { surface = surface.Name, description = operation.Description, breaklines = operation.Count, operations = surface.Operations.Count };
}

return new
{
    success = true,
    alignment = alignment.Name,
    pointsRead = points.Count,
    withoutHeight,
    filteredOut,
    outsideAlignment = outside,
    beyondMaxOffset = tooFar,
    scanTruncated = truncated,
    xrefsRead,
    stringsFound = strings.Count,
    stringsDrawn = kept.Count,
    droppedShortStrings = strings.Count - kept.Count,
    newStringsAfterLongTie = longTies,
    newStringsAfterLateralJump = lateralJumps,
    byCode = kept.GroupBy(s => s.key).ToDictionary(g => g.Key, g => g.Count()),
    layer = polylineLayer,
    layerCreated,
    polylines = rows,
    breaklines
};
```

## Usage Notes

- Write recipe: run it first with `preview: true` and read `stringsDrawn`, `byCode` and the polylines (station range, mean offset, length); adjust the codes, `maxTieLength` and `maxLateralJump`; then run it for real with `requireClean`, `backup` and `saveDrawing`.
- Points are grouped by the surveyor's code (`codeAttributeTag`, or a COGO point's raw description) or, without a tag, by layer. Inside a group, points are taken in station order along the alignment; a point continues the open string whose last point is closest in offset, when the plan distance is at most `maxTieLength` and the offset changes by at most `maxLateralJump`. So two kerbs with the same code on the two sides of the road become two strings, and a gap in the survey (a gate, a missing stretch) starts a new string instead of a long straight tie.
- A point without a height is left out (`withoutHeight`); with `elevationAttributeTag` the height is the number in that attribute, for a survey drawn flat. Points on the same spot count once.
- With `surfaceHandle` all new polylines go into the TIN surface as one standard breakline operation with `breaklineDescription`, kept apart from the surface's other breaklines, so it can be removed in one step. The surface is rebuilt by Civil 3D as usual; read it back with `surface_definition_summary` or `saved_drawing_readback`.
- Nothing is changed in xrefs or in the survey points; only new 3D polylines (and the layer when missing) are added in this drawing. Which codes form a terrain break is the caller's engineering decision.
- The defaults (30 for the longest tie, 0.8 for the largest sideways jump) come from field use on a street survey (Civil 3D 2025), where longer ties or bigger jumps joined points that did not belong together.
