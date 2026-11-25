# Preview receipt after 3D polylines, live check (2026-11-25)

**Proven:** with the plugin and server built from `7d6b2d0`, Civil 3D 2025 (Hungarian install,
`C3D_Hungary.arg` profile, stock `_Autodesk Civil 3D (Metric) NCS.dwt`) on the owner's work PC:

- `npm run smoke:live -- --start-civil` passed: 35 cases passed, 1 warning (`W2b`, expected),
  0 failed, in 26.0 s. Plugin and server came from the same build (`S1b`).
- `W10` (preview of a new layer with three 3D polylines): `rolledBack: true`, `dbmodAfter` 0;
  a readback found no layer, no 3D polyline and `DBMOD` 0.

## What the old check counted

A diagnostic in the same instance and drawing repeated that preview and then read every handle
the preview had given out (22 of them):

| Class | Count | Id marked erased | Opened, erased flag | Owner |
| --- | --- | --- | --- | --- |
| `AcDbLayerTableRecord` | 1 | yes | erased | layer table (old) |
| `AcDb3dPolyline` | 3 | yes | erased | model space (old) |
| `AcDb3dPolylineVertex` | 15 | yes | erased | its polyline (new, erased) |
| `AcDbSequenceEnd` | 3 | **no** | **not erased** | its polyline (new, erased) |

The old check counted every new handle whose id was not marked erased, so it would have
reported 3 here: the end markers of the three polylines. That matches the field case (196
polylines, `leftAfterPreview: 196`). The new check follows the end marker to its owner, finds
the owner erased and does not count it. The receipt of this repeat said `rolledBack: true`
and `DBMOD` stayed 0.

## Not covered

- Other complex entities with an end marker (old-style 2D polylines, block references with
  attributes) were not tried; they go through the same owner rule.
- A failed (not previewed) script that created 3D polylines (`leftBehind`) was not run live;
  it uses the same count.
