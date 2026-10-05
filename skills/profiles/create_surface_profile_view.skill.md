---
name: create_surface_profile_view
category: profiles
description: Create one full-length dynamic TIN surface profile and one ordinary profile view from explicitly identified local sources
requires_write: true
aliases: ["terep hossz-szelvény és profilnézet létrehozása", "create TIN surface profile and profile view", "felületi hossz-szelvény megjelenítése nyomvonalon"]
workflow_tags: ["drawing_view", "modeling"]
tested_civil_version: "2025"
validation_summary: "Offline: 2025 compilation and input checks. Live creation, save and reopen unverified; setup timed out before this recipe ran."
parameters:
  - name: alignmentHandle
    type: string
    required: true
    description: Hex handle of an existing local Alignment
  - name: surfaceHandle
    type: string
    required: true
    description: Hex handle of an existing local TinSurface
  - name: profileName
    type: string
    required: true
    description: New unique dynamic ground-profile name
  - name: profileViewName
    type: string
    required: true
    description: New unique ordinary profile-view name
  - name: insertionX
    type: number
    required: true
    description: Profile-view insertion X in drawing coordinates
  - name: insertionY
    type: number
    required: true
    description: Profile-view insertion Y in drawing coordinates
  - name: profileStyleName
    type: string
    required: false
    description: Existing profile style name; defaults to Terep
  - name: profileViewStyleName
    type: string
    required: false
    description: Existing profile-view style name; defaults to Út
  - name: profileLabelSetName
    type: string
    required: true
    description: Explicit existing profile label-set name
  - name: bandSetStyleName
    type: string
    required: true
    description: Explicit existing profile-view band-set style name
---

## Code Template

```csharp
var alignmentHandle = "ALIGNMENT_HANDLE";
var surfaceHandle = "SURFACE_HANDLE";
var profileName = "NEW_SURFACE_PROFILE_NAME";
var profileViewName = "NEW_PROFILE_VIEW_NAME";
var insertionX = 0.0;
var insertionY = 0.0;
var profileStyleName = "Terep";
var profileViewStyleName = "Út";
var profileLabelSetName = "PROFILE_LABEL_SET_NAME";
var bandSetStyleName = "PROFILE_VIEW_BAND_SET_STYLE_NAME";

var isHexHandle = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value)
    && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9A-Fa-f]{1,16}$"));
var isName = new Func<string, bool>(value =>
    !string.IsNullOrWhiteSpace(value)
    && value == value.Trim()
    && value.Length <= 255
    && !value.Any(char.IsControl));
var isConfiguredName = new Func<string, bool>(value =>
    isName(value)
    && value != "NEW_SURFACE_PROFILE_NAME"
    && value != "NEW_PROFILE_VIEW_NAME"
    && value != "PROFILE_LABEL_SET_NAME"
    && value != "PROFILE_VIEW_BAND_SET_STYLE_NAME");

if (!isHexHandle(alignmentHandle))
    return new { success = false, error = "alignmentHandle must be a non-empty hexadecimal handle." };
if (!isHexHandle(surfaceHandle))
    return new { success = false, error = "surfaceHandle must be a non-empty hexadecimal handle." };
if (!isConfiguredName(profileName) || !isConfiguredName(profileViewName))
    return new { success = false, error = "profileName and profileViewName must be configured trimmed non-empty names of at most 255 characters without control characters." };
if (!Double.IsFinite(insertionX) || !Double.IsFinite(insertionY))
    return new { success = false, error = "insertionX and insertionY must be finite numbers." };
if (!isName(profileStyleName) || !isName(profileViewStyleName))
    return new { success = false, error = "profileStyleName and profileViewStyleName must be trimmed non-empty names of at most 255 characters without control characters." };
if (!isConfiguredName(profileLabelSetName) || !isConfiguredName(bandSetStyleName))
    return new { success = false, error = "profileLabelSetName and bandSetStyleName must be explicitly configured trimmed non-empty names." };

// Host access starts here.
ObjectId alignmentId;
ObjectId surfaceId;
try
{
    alignmentId = Database.GetObjectId(false, new Handle(System.Convert.ToInt64(alignmentHandle, 16)), 0);
    surfaceId = Database.GetObjectId(false, new Handle(System.Convert.ToInt64(surfaceHandle, 16)), 0);
}
catch
{
    return new { success = false, error = "alignmentHandle or surfaceHandle does not resolve to an object in this drawing." };
}

var alignment = Transaction.GetObject(alignmentId, OpenMode.ForRead) as Alignment;
if (alignment == null || alignment.IsErased)
    return new { success = false, error = "alignmentHandle must identify an existing Alignment." };
if (alignment.IsReferenceObject)
    return new { success = false, error = "alignmentHandle must identify a local, non-reference Alignment." };
if (alignment.IsReferenceStale)
    return new { success = false, error = "alignmentHandle identifies a stale Alignment reference and is refused." };

var surface = Transaction.GetObject(surfaceId, OpenMode.ForRead) as TinSurface;
if (surface == null || surface.IsErased || surface is TinVolumeSurface)
    return new { success = false, error = "surfaceHandle must identify an existing TinSurface (not a grid or volume surface)." };
if (surface.IsReferenceObject)
    return new { success = false, error = "surfaceHandle must identify a local, non-reference TinSurface." };
if (surface.IsReferenceStale)
    return new { success = false, error = "surfaceHandle identifies a stale TinSurface reference and is refused." };
var rangeTolerance = 1e-6;
if (!Double.IsFinite(alignment.StartingStation)
    || !Double.IsFinite(alignment.EndingStation)
    || alignment.EndingStation - alignment.StartingStation <= rangeTolerance)
    return new { success = false, error = "The alignment must have a finite, positive parent station range." };
if (alignment.LayerId.IsNull)
    return new { success = false, error = "The alignment has no usable layer to reuse for the new profile." };

foreach (ObjectId existingProfileId in alignment.GetProfileIds())
{
    var existingProfile = Transaction.GetObject(existingProfileId, OpenMode.ForRead) as Profile;
    if (existingProfile != null && !existingProfile.IsErased
        && existingProfile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
        return new { success = false, error = "A profile with this name already exists on the selected alignment.", profileName };
}
foreach (ObjectId existingViewId in alignment.GetProfileViewIds())
{
    var existingView = Transaction.GetObject(existingViewId, OpenMode.ForRead) as ProfileView;
    if (existingView != null && !existingView.IsErased
        && existingView.Name.Equals(profileViewName, StringComparison.OrdinalIgnoreCase))
        return new { success = false, error = "A profile view with this name already exists on the selected alignment.", profileViewName };
}

ObjectId profileStyleId;
ObjectId profileLabelSetId;
ObjectId profileViewStyleId;
ObjectId bandSetStyleId;
try
{
    profileStyleId = CivilDoc.Styles.ProfileStyles[profileStyleName];
    profileLabelSetId = CivilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles[profileLabelSetName];
    profileViewStyleId = CivilDoc.Styles.ProfileViewStyles[profileViewStyleName];
    bandSetStyleId = CivilDoc.Styles.ProfileViewBandSetStyles[bandSetStyleName];
}
catch
{
    return new { success = false, error = "One or more explicitly named profile, profile-label-set, profile-view, or profile-view-band-set styles are unavailable." };
}
if (profileStyleId.IsNull || profileLabelSetId.IsNull || profileViewStyleId.IsNull || bandSetStyleId.IsNull)
    return new { success = false, error = "One or more explicitly named styles resolved to an invalid object ID." };

var profileId = Profile.CreateFromSurface(
    profileName,
    alignmentId,
    surfaceId,
    alignment.LayerId,
    profileStyleId,
    profileLabelSetId
);
if (profileId.IsNull)
    throw new InvalidOperationException("Surface-profile creation returned an invalid object ID.");

var profile = Transaction.GetObject(profileId, OpenMode.ForWrite) as Profile;
if (profile == null || profile.IsErased)
    throw new InvalidOperationException("Surface-profile creation did not return a readable profile.");
profile.UpdateMode = ProfileUpdateType.Dynamic;
if (!profile.Name.Equals(profileName, StringComparison.Ordinal)
    || profile.AlignmentId != alignmentId
    || profile.StyleId != profileStyleId
    || profile.ProfileType != ProfileType.EG
    || profile.UpdateMode != ProfileUpdateType.Dynamic
    || profile.Entities.Count == 0
    || !Double.IsFinite(profile.StartingStation)
    || !Double.IsFinite(profile.EndingStation)
    || Math.Abs(profile.StartingStation - alignment.StartingStation) > rangeTolerance
    || Math.Abs(profile.EndingStation - alignment.EndingStation) > rangeTolerance)
    throw new InvalidOperationException("The created dynamic surface profile did not satisfy its postconditions.");
var startElevation = profile.ElevationAt(profile.StartingStation);
var endElevation = profile.ElevationAt(profile.EndingStation);
if (!Double.IsFinite(startElevation) || !Double.IsFinite(endElevation))
    throw new InvalidOperationException("The created dynamic surface profile does not return finite endpoint elevations.");

// Civil 3D 2025 API lookup proves this overload orders band-set style, then profile-view style.
var viewId = ProfileView.Create(
    alignmentId,
    new Point3d(insertionX, insertionY, 0.0),
    profileViewName,
    profileViewBandSetId: bandSetStyleId,
    profileViewStyleId: profileViewStyleId
);
if (viewId.IsNull)
    throw new InvalidOperationException("Profile-view creation returned an invalid object ID.");

var view = Transaction.GetObject(viewId, OpenMode.ForRead) as ProfileView;
if (view == null || view.IsErased
    || !view.Name.Equals(profileViewName, StringComparison.Ordinal)
    || view.AlignmentId != alignmentId
    || view.StyleId != profileViewStyleId)
    throw new InvalidOperationException("The created profile view did not satisfy its postconditions.");

return new {
    success = true,
    profile = new {
        handle = profile.Handle.ToString(),
        name = profile.Name,
        alignmentHandle = alignment.Handle.ToString(),
        surfaceHandle = surface.Handle.ToString(),
        layer = profile.Layer,
        style = profile.StyleName,
        type = profile.ProfileType.ToString(),
        updateMode = profile.UpdateMode.ToString(),
        startStation = profile.StartingStation,
        endStation = profile.EndingStation,
        startElevation,
        endElevation
    },
    profileView = new {
        handle = view.Handle.ToString(),
        name = view.Name,
        style = view.StyleName,
        requestedInsertion = new { x = insertionX, y = insertionY, z = 0.0 }
    }
};
```

## Usage Notes

- This is a write-capable local-only recipe. First make a fresh `civil3d_query` drawing guard for the full `Database.Filename` and `FingerprintGuid`; verify the alignment and TIN source handles in that same identified drawing.
- Make a timestamped unchanged filesystem backup before the write. Execute once with `saveDrawing: true`, then perform a separate bounded readback that confirms the exact returned objects and `DBMOD=0`. Do not blindly retry a timeout or a failed compile/execution response.
- Existing `Terep` and `Út` are the respective defaults from the proven local baseline. The profile label set and profile-view band set remain explicit because their names are project/template choices. The selected sets can create their own label or band content; this recipe creates no styles, changes no styles, and adds no custom band source mapping.
- The profile is `Profile.CreateFromSurface` at offset zero over the parent alignment's full station range, and must return a dynamic EG/surface profile with finite matching endpoints before a view is created. Endpoint/range checks do not prove there are no interior surface gaps. This deliberately excludes offsets, a designed grade, style creation, split or stacked views, and engineering-compliance decisions.
- Both sources must be local, non-reference, non-stale objects. This narrow first scope rejects rather than dereferences external/DREF state, so no unavailable source can be silently substituted.
- Duplicate names are rejected before creation and no existing profile, profile view, source, or style is modified. The host-provided outer transaction owns commit/rollback; this template never calls `Commit`, `SaveAs`, or `QSAVE`, and postcondition failures throw so the outer transaction can abort.
- **Proven offline (2026-09-03):** the template compiles against Civil 3D 2025 metadata, and 11 host-free input checks pass. The loaded 2025 API lookup also confirmed the view overload's band-set/style parameter names; named arguments keep those same-typed IDs explicit. **Unverified live:** the separate synthetic-terrain setup call timed out before this recipe was invoked. No profile/view creation or saved/reopened result is claimed.
