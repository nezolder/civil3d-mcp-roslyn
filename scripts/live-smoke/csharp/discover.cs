// Live smoke test, read only: identity of the active drawing, whether it is
// still free of Civil 3D objects, and the first style names the fixture uses.
// Names are picked from the drawing, so the fixture works with any template.
Func<System.Collections.IEnumerable, string> firstName = ids =>
{
    if (ids == null) return null;
    foreach (var item in ids)
    {
        if (!(item is ObjectId id) || id.IsNull || id.IsErased) continue;
        var style = Transaction.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Styles.StyleBase;
        if (style != null && !string.IsNullOrEmpty(style.Name)) return style.Name;
    }
    return null;
};

var modelSpace = (BlockTableRecord)Transaction.GetObject(
    SymbolUtilityServices.GetBlockModelSpaceId(Database), OpenMode.ForRead);
var modelSpaceEntities = 0;
foreach (ObjectId id in modelSpace) modelSpaceEntities++;

var styles = CivilDoc.Styles;
var labelSets = styles.LabelSetStyles;

return new {
    databaseFilename = Database.Filename,
    fingerprintGuid = Database.FingerprintGuid.ToString(),
    dbmod = System.Convert.ToInt32(Application.GetSystemVariable("DBMOD")),
    drawingUnits = CivilDoc.Settings.DrawingSettings.UnitZoneSettings.DrawingUnits.ToString(),
    counts = new {
        surfaces = CivilDoc.GetSurfaceIds().Count,
        alignments = CivilDoc.GetAlignmentIds().Count,
        corridors = CivilDoc.CorridorCollection.Count,
        assemblies = CivilDoc.AssemblyCollection.Count,
        modelSpaceEntities
    },
    styles = new {
        alignmentStyle = firstName(styles.AlignmentStyles),
        alignmentLabelSet = firstName(labelSets.AlignmentLabelSetStyles),
        profileStyle = firstName(styles.ProfileStyles),
        profileLabelSet = firstName(labelSets.ProfileLabelSetStyles),
        profileViewStyle = firstName(styles.ProfileViewStyles),
        profileViewBandSet = firstName(styles.ProfileViewBandSetStyles),
        sectionStyle = firstName(styles.SectionStyles)
    }
};
