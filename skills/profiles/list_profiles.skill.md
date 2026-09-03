---
name: list_profiles
category: profiles
description: List existing profiles with alignment, station range, elevation range, and reference state
requires_write: false
parameters:
  - name: alignmentName
    type: string
    required: false
    description: Optional exact alignment name filter
  - name: limit
    type: int
    required: false
    description: Maximum profiles to return (default 100)
---

## Code Template

```csharp
var alignmentNameFilter = "";
var limit = 100;
if (limit < 1) return new { error = "limit must be at least 1" };

var profiles = new List<object>();
var total = 0;

foreach (ObjectId alignmentId in CivilDoc.GetAlignmentIds())
{
    var alignment = Transaction.GetObject(alignmentId, OpenMode.ForRead) as Alignment;
    if (alignment == null) continue;
    if (!string.IsNullOrWhiteSpace(alignmentNameFilter)
        && !alignment.Name.Equals(alignmentNameFilter, StringComparison.OrdinalIgnoreCase))
        continue;

    var profileIds = alignment.GetProfileIds();
    total += profileIds.Count;

    foreach (ObjectId profileId in profileIds)
    {
        if (profiles.Count >= limit) continue;
        var profile = Transaction.GetObject(profileId, OpenMode.ForRead) as Profile;
        if (profile == null) continue;

        profiles.Add(new {
            alignmentName = alignment.Name,
            name = profile.Name,
            handle = profile.Handle.ToString(),
            type = profile.ProfileType.ToString(),
            startStation = profile.StartingStation,
            endStation = profile.EndingStation,
            minElevation = profile.ElevationMin,
            maxElevation = profile.ElevationMax,
            style = profile.StyleName,
            entityCount = profile.Entities.Count,
            pviCount = profile.PVIs.Count,
            isReference = profile.IsReferenceObject,
            isReferenceStale = profile.IsReferenceStale
        });
    }
}

return new {
    total,
    returned = profiles.Count,
    truncated = total > profiles.Count,
    limit,
    alignmentNameFilter = string.IsNullOrWhiteSpace(alignmentNameFilter) ? null : alignmentNameFilter,
    profiles
};
```

## Usage Notes
- Leave `alignmentNameFilter` empty to inspect every alignment
- Run through `civil3d_query`; this skill opens profiles only for reading
- Result size is bounded by `limit`
