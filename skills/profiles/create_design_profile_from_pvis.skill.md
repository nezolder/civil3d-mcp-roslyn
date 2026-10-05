---
name: create_design_profile_from_pvis
category: profiles
description: Create one local design profile from explicit ordered PVIs and optional symmetric parabolic curves
requires_write: true
aliases: ["tervezett hossz-szelvény létrehozása jóváhagyott PVIkből", "create design profile from ordered PVIs and curve lengths", "hossz-szelvény készítése megadott töréspontokból"]
workflow_tags: ["modeling"]
tested_civil_version: "2025"
validation_summary: "Offline + live: three PVIs and one symmetric parabola, save and independent reopen. Broader curves and engineering compliance unverified."
parameters:
  - name: alignmentHandle
    type: string
    required: true
    description: Hex handle of a local alignment without station equations
  - name: profileName
    type: string
    required: true
    description: Unique name for the new design profile
  - name: profileStyleName
    type: string
    required: true
    description: Existing profile style name
  - name: profileLabelSetName
    type: string
    required: true
    description: Existing profile label-set style name
  - name: pviData
    type: array
    required: true
    description: 2 to 200 ordered station/elevation/curveLength tuples in drawing units
---

## Code Template

```csharp
var alignmentHandle = "ALIGNMENT_HANDLE";
var profileName = "NEW_DESIGN_PROFILE_NAME";
var profileStyleName = "PROFILE_STYLE_NAME";
var profileLabelSetName = "PROFILE_LABEL_SET_NAME";
var pviData = new (double station, double elevation, double curveLength)[] { };
var tolerance = 1e-6;

var isHandle = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value)
    && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{1,16}$")
    && long.TryParse(value, System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0);
var isName = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 200
    && !value.Any(char.IsControl));
if (!isHandle(alignmentHandle))
    return new { success = false, error = "Configure a positive hexadecimal alignmentHandle." };
if (!isName(profileName) || profileName == "NEW_DESIGN_PROFILE_NAME"
    || !isName(profileStyleName) || profileStyleName == "PROFILE_STYLE_NAME"
    || !isName(profileLabelSetName) || profileLabelSetName == "PROFILE_LABEL_SET_NAME")
    return new { success = false, error = "Configure explicit profile and existing style names." };
if (pviData == null || pviData.Length < 2 || pviData.Length > 200)
    return new { success = false, error = "pviData must contain 2 to 200 points." };
for (var i = 0; i < pviData.Length; i++)
{
    var p = pviData[i];
    if (!Double.IsFinite(p.station) || !Double.IsFinite(p.elevation)
        || !Double.IsFinite(p.curveLength) || p.curveLength < 0)
        return new { success = false, error = "PVI coordinates and nonnegative curve lengths must be finite." };
    if ((i == 0 || i == pviData.Length - 1) && p.curveLength != 0)
        return new { success = false, error = "Endpoint PVIs cannot have vertical curves." };
    if (i > 0)
    {
        var previous = pviData[i - 1];
        var span = p.station - previous.station;
        var grade = (p.elevation - previous.elevation) / span;
        if (!Double.IsFinite(span) || span <= tolerance || !Double.IsFinite(grade))
            return new { success = false, error = "PVI stations must be strictly increasing with finite tangent grades." };
        if (previous.station + previous.curveLength / 2 >= p.station - p.curveLength / 2 - tolerance)
            return new { success = false, error = "Adjacent vertical curves must leave a positive tangent gap." };
    }
    if (p.curveLength > 0 && i > 0 && i < pviData.Length - 1)
    {
        var before = pviData[i - 1];
        var after = pviData[i + 1];
        if (p.station - p.curveLength / 2 <= before.station + tolerance
            || p.station + p.curveLength / 2 >= after.station - tolerance)
            return new { success = false, error = "A symmetric curve must fit strictly between its neighboring PVIs." };
        var gradeIn = (p.elevation - before.elevation) / (p.station - before.station);
        var gradeOut = (after.elevation - p.elevation) / (after.station - p.station);
        if (!Double.IsFinite(gradeIn) || !Double.IsFinite(gradeOut) || Math.Abs(gradeOut - gradeIn) <= 1e-12)
            return new { success = false, error = "A vertical curve requires two distinct finite tangent grades." };
    }
}

// Host access starts here.
ObjectId alignmentId;
try { alignmentId = Database.GetObjectId(false, new Handle(Convert.ToInt64(alignmentHandle, 16)), 0); }
catch { return new { success = false, error = "alignmentHandle does not resolve in this drawing." }; }
var alignment = Transaction.GetObject(alignmentId, OpenMode.ForRead) as Alignment;
if (alignment == null || alignment.IsErased || alignment.IsReferenceObject || alignment.StationEquations.Count != 0)
    return new { success = false, error = "A local alignment without station equations is required." };
if (!Double.IsFinite(alignment.StartingStation) || !Double.IsFinite(alignment.EndingStation)
    || pviData[0].station < alignment.StartingStation || pviData[pviData.Length - 1].station > alignment.EndingStation)
    return new { success = false, error = "All PVIs must be inside the alignment station range." };
foreach (ObjectId id in alignment.GetProfileIds())
{
    var existing = Transaction.GetObject(id, OpenMode.ForRead) as Profile;
    if (existing != null && !existing.IsErased && existing.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
        return new { success = false, error = "This profile name already exists on the selected alignment." };
}
ObjectId styleId;
ObjectId labelsId;
try
{
    styleId = CivilDoc.Styles.ProfileStyles[profileStyleName];
    labelsId = CivilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles[profileLabelSetName];
}
catch { return new { success = false, error = "The explicitly named profile or label-set style is unavailable." }; }
if (styleId.IsNull || labelsId.IsNull || alignment.LayerId.IsNull)
    return new { success = false, error = "Valid existing styles and alignment layer are required." };

var profileId = Profile.CreateByLayout(profileName, alignmentId, alignment.LayerId, styleId, labelsId);
var profile = Transaction.GetObject(profileId, OpenMode.ForWrite) as Profile;
if (profile == null) throw new InvalidOperationException("Design-profile creation returned no readable Profile.");
foreach (var p in pviData) profile.PVIs.AddPVI(p.station, p.elevation);
for (var i = 1; i < pviData.Length - 1; i++)
    if (pviData[i].curveLength > 0)
        profile.Entities.AddFreeSymmetricParabolaByPVIAndCurveLength(profile.PVIs[i], pviData[i].curveLength);

if (profile.ProfileType != ProfileType.FG || profile.AlignmentId != alignmentId
    || profile.StyleId != styleId || profile.PVIs.Count != pviData.Length
    || profile.Name != profileName || profile.Entities.Count == 0
    || Math.Abs(profile.StartingStation - pviData[0].station) > tolerance
    || Math.Abs(profile.EndingStation - pviData[pviData.Length - 1].station) > tolerance)
    throw new InvalidOperationException("The created design profile failed its identity/range postconditions.");
var points = new List<object>();
for (var i = 0; i < pviData.Length; i++)
{
    var expected = pviData[i];
    var actual = profile.PVIs[i];
    if (Math.Abs(actual.Station - expected.station) > tolerance || Math.Abs(actual.Elevation - expected.elevation) > tolerance)
        throw new InvalidOperationException("The saved geometry would not match the requested PVI coordinates.");
    if (expected.curveLength > 0)
    {
        var curve = actual.VerticalCurve as ProfileParabolaSymmetric;
        if (curve == null || Math.Abs(curve.StartStation - (expected.station - expected.curveLength / 2)) > tolerance
            || Math.Abs(curve.EndStation - (expected.station + expected.curveLength / 2)) > tolerance)
            throw new InvalidOperationException("A vertical curve failed its type/extent postconditions.");
    }
    points.Add(new { station = actual.Station, elevation = actual.Elevation, curveLength = expected.curveLength });
}
return new {
    success = true, handle = profile.Handle.ToString(), name = profile.Name,
    alignmentHandle = alignment.Handle.ToString(), type = profile.ProfileType.ToString(),
    style = profile.StyleName, units = CivilDoc.Settings.DrawingSettings.UnitZoneSettings.DrawingUnits.ToString(),
    startStation = profile.StartingStation, endStation = profile.EndingStation,
    pviCount = profile.PVIs.Count, curveCount = pviData.Count(p => p.curveLength > 0), pvis = points
};
```

## Usage Notes

- This is a configured recipe, not a design generator: the caller supplies the approved station/elevation/curve-length data in the verified drawing units and vertical datum. There are no invented default PVIs, grade limits, K-values, or road-standard assumptions.
- Read the exact alignment handle, styles, full `Database.Filename` and `FingerprintGuid` with `civil3d_query` first. Require a named clean drawing (`DBMOD=0`) and make an unchanged timestamped filesystem backup. Execute once with the matching `expectedDrawing` and `saveDrawing: true`.
- The first scope is a new local FG profile, with straight grades and optional symmetric parabolas. Endpoint curves, coincident grades with curves, touching/overlapping curves and station equations are refused. This does not change an existing profile, corridor, profile view or source alignment.
- The host owns transaction/rollback/save. A failed postcondition throws before commit. After success, independently query the returned profile handle, PVI coordinates, curve extents, styles, range and `DBMOD=0`; reopen a saved test copy when validating the recipe. Reconcile an uncertain outcome before another write.
- The PVI/curve API idea is informed by [Mmpasta00's MIT recipe](https://github.com/Mmpasta00/civil3d-mcp/blob/618fe787ca414f551e359eac02cc9fe6c6af61ad/skills/profiles/create_design_profile_from_pvis.skill.md). This implementation is independently written with explicit 2025 API members, preflight and postconditions; it copies no upstream code body.
- **Proven in a scoped Civil 3D 2025 live test (2026-10-01):** a new three-PVI FG profile with one 40-unit symmetric parabola survived host-managed save and independent close/reopen. PVI coordinates, curve extents, sampled elevations and `DBMOD=0` matched. Broader curve combinations, road-standard compliance and engineering suitability remain **Unverified**.
