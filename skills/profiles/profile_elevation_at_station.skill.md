---
name: profile_elevation_at_station
category: profiles
description: Read the elevation of an existing profile at one station
requires_write: false
parameters:
  - name: alignmentName
    type: string
    required: true
    description: Exact alignment name
  - name: profileName
    type: string
    required: true
    description: Exact profile name on the alignment
  - name: station
    type: double
    required: true
    description: Station to evaluate in drawing units
---

## Code Template

```csharp
var alignmentName = "ALIGNMENT_NAME";
var profileName = "PROFILE_NAME";
var station = 100.0;

Alignment alignment = null;
foreach (ObjectId alignmentId in CivilDoc.GetAlignmentIds())
{
    var candidate = Transaction.GetObject(alignmentId, OpenMode.ForRead) as Alignment;
    if (candidate != null && candidate.Name.Equals(alignmentName, StringComparison.OrdinalIgnoreCase))
    {
        alignment = candidate;
        break;
    }
}
if (alignment == null) return new { error = "Alignment not found", alignmentName };

Profile profile = null;
foreach (ObjectId profileId in alignment.GetProfileIds())
{
    var candidate = Transaction.GetObject(profileId, OpenMode.ForRead) as Profile;
    if (candidate != null && candidate.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
    {
        profile = candidate;
        break;
    }
}
if (profile == null) return new { error = "Profile not found", alignmentName, profileName };

if (station < profile.StartingStation || station > profile.EndingStation)
{
    return new {
        error = "Station is outside the profile range",
        alignmentName,
        profileName,
        station,
        startStation = profile.StartingStation,
        endStation = profile.EndingStation
    };
}

return new {
    alignmentName,
    profileName,
    profileType = profile.ProfileType.ToString(),
    station,
    elevation = profile.ElevationAt(station),
    startStation = profile.StartingStation,
    endStation = profile.EndingStation
};
```

## Usage Notes
- The station must use the alignment's stationing convention
- This reads an existing profile and does not create profile views or geometry
