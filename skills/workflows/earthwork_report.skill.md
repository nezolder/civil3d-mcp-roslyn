---
name: earthwork_report
category: workflows
description: Summarize an existing ground surface, alignments, and existing profiles in a read-only report
requires_write: false
parameters:
  - name: surfaceName
    type: string
    required: true
    description: The existing ground surface name
---

## Code Template

```csharp
// Find the EG surface
TinSurface egSurface = null;
foreach (ObjectId id in CivilDoc.GetSurfaceIds())
{
    var s = Transaction.GetObject(id, OpenMode.ForRead) as TinSurface;
    if (s != null && s.Name.Equals("SURFACE_NAME", StringComparison.OrdinalIgnoreCase))
    { egSurface = s; break; }
}
if (egSurface == null) return new { error = "Surface not found" };

// Iterate all alignments and gather data
var report = new List<object>();

foreach (ObjectId alignId in CivilDoc.GetAlignmentIds())
{
    var alignment = Transaction.GetObject(alignId, OpenMode.ForRead) as Alignment;
    if (alignment == null) continue;

    // Get profiles for this alignment
    var profiles = new List<object>();
    foreach (ObjectId profId in alignment.GetProfileIds())
    {
        var profile = Transaction.GetObject(profId, OpenMode.ForRead) as Profile;
        if (profile == null) continue;
        profiles.Add(new {
            name = profile.Name,
            type = profile.ProfileType.ToString(),
            startStation = profile.StartingStation,
            endStation = profile.EndingStation
        });
    }

    report.Add(new {
        alignmentName = alignment.Name,
        length = alignment.Length,
        startStation = alignment.StartingStation,
        endStation = alignment.EndingStation,
        profileCount = profiles.Count,
        profiles
    });
}

return new {
    surfaceName = egSurface.Name,
    surfaceStats = new {
        minElevation = egSurface.GetGeneralProperties().MinimumElevation,
        maxElevation = egSurface.GetGeneralProperties().MaximumElevation,
        pointCount = egSurface.GetGeneralProperties().NumberOfPoints
    },
    alignmentCount = report.Count,
    alignments = report
};
```

## Usage Notes
- This is an inventory report; it does not create profiles or compute cut/fill volumes
- Adapt the selected surface name and returned fields to the specific use case
- Combine with station_offset skill for detailed station-by-station analysis
