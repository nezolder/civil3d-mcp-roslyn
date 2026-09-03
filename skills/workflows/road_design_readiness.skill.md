---
name: road_design_readiness
category: workflows
description: Check whether the active drawing has the proven MT_2025 Hungary baseline styles for a basic road-design workflow
requires_write: false
parameters: []
---

## Code Template

```csharp
var dbmod = Convert.ToInt32(
    Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("DBMOD")
);

var buildNamedGroup = (Func<string, object> lookup, int total, string[] requiredNames) =>
{
    var missing = new List<string>();
    var sample = new List<string>();

    foreach (var name in requiredNames)
    {
        var found = false;
        try
        {
            var value = lookup(name);
            found = value != null && (value is not ObjectId id || !id.IsNull);
        }
        catch
        {
            // A missing style can be reported without depending on a public name getter.
        }

        if (found)
        {
            if (sample.Count < 3) sample.Add(name);
        }
        else
        {
            missing.Add(name);
        }
    }

    return new {
        total,
        required = requiredNames.Length,
        missing,
        ready = missing.Count == 0,
        sample,
        truncated = total > sample.Count
    };
};

var buildMinimumGroup = (int total) =>
{
    var missing = new List<string>();
    if (total < 1) missing.Add("at least one style");
    var sample = new List<string>();

    return new {
        total,
        required = 1,
        missing,
        ready = total >= 1,
        sample,
        truncated = total > 0
    };
};

var styles = CivilDoc.Styles;
var alignment = buildNamedGroup(
    name => styles.AlignmentStyles[name],
    styles.AlignmentStyles.Count,
    new[] { "Tervező" }
);
var profile = buildNamedGroup(
    name => styles.ProfileStyles[name],
    styles.ProfileStyles.Count,
    new[] { "Terep", "Tervező" }
);
var profileView = buildNamedGroup(
    name => styles.ProfileViewStyles[name],
    styles.ProfileViewStyles.Count,
    new[] { "Út" }
);
var assembly = buildNamedGroup(
    name => styles.AssemblyStyles[name],
    styles.AssemblyStyles.Count,
    new[] { "Szabványos" }
);
var codeSet = buildNamedGroup(
    name => styles.CodeSetStyles[name],
    styles.CodeSetStyles.Count,
    new[] { "Nyomterv tervező", "Nyomterv KSZ nyomtatás" }
);
var corridor = buildMinimumGroup(styles.CorridorStyles.Count);
var sampleLine = buildNamedGroup(
    name => styles.SampleLineStyles[name],
    styles.SampleLineStyles.Count,
    new[] { "Út mintavonal" }
);
var section = buildNamedGroup(
    name => styles.SectionStyles[name],
    styles.SectionStyles.Count,
    new[] { "Meglévő terep", "Tervezett pálya" }
);
var sectionView = buildNamedGroup(
    name => styles.SectionViewStyles[name],
    styles.SectionViewStyles.Count,
    new[] { "Út keresztszelvény rajz" }
);
var surface = buildNamedGroup(
    name => styles.SurfaceStyles[name],
    styles.SurfaceStyles.Count,
    new[] { "Szabványos" }
);
var partsList = buildNamedGroup(
    name => styles.PartsListSet[name],
    styles.PartsListSet.Count,
    new[] { "Szabványos" }
);
var superelevationView = buildMinimumGroup(styles.SuperelevationViewStyles.Count);

var groups = new {
    alignment,
    profile,
    profileView,
    assembly,
    codeSet,
    corridor,
    sampleLine,
    section,
    sectionView,
    surface,
    partsList,
    superelevationView
};

var readiness = new[] {
    alignment.ready,
    profile.ready,
    profileView.ready,
    assembly.ready,
    codeSet.ready,
    corridor.ready,
    sampleLine.ready,
    section.ready,
    sectionView.ready,
    surface.ready,
    partsList.ready,
    superelevationView.ready
};

return new {
    overallReady = readiness.All(value => value),
    dbmod,
    groups
};
```

## Usage Notes
- This is a read-only readiness check; it does not create, change, or save styles or drawing data.
- The required names come from the proven `MT_2025.dwt` Hungary baseline; they are not presented as a complete office standard.
- Named groups use each collection's string indexer so the check does not require enumerating style names through a managed getter.
- `sample` contains at most three verified required names; `truncated` means the collection has additional styles beyond that small sample.
- Corridor and superelevation-view readiness currently requires at least one style because their installed style names were not proven stable in the baseline probe.
