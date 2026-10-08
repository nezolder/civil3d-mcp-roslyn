# Offset station locking: Civil 3D 2025 validation

Tested implementation: `a3dc595857e2f2b804f3747c0c341e17aec4a3c4`, branch `claude/kind-volta-rsvbm5`. Date: 2026-10-08.

**Proven:** named `create_offset_alignments` with `lockToStations: true` retained parent stations 1200..2600 after an actual native PI grip edit changed the parent's length, and after save/close/reopen.

## Preparation

The requested branch was pulled with a fast-forward; `git log -1` was exactly `a3dc595` before testing. `npm run build` passed. `npm run test:skills` passed: 32 Node tests, compilation of all 36 C# templates against authentic installed Civil 3D 2025 assemblies, and 83 host-free cases (18 + 11 + 54).

Fresh MCP stdio servers from the updated local build performed the probes. The final fresh server exposed exactly three tools and 33 skills, read the new recipe, completed `return 1;`, and returned to idle with an empty queue. The registered desktop connector also returned the updated recipe containing the station-locking option. Existing desktop clients were not forcibly restarted; a client reconnect/restart remains necessary for any older persistent process that still needs the earlier SDK update.

Only recipe files changed in this commit; plugin and Node implementation sources are unchanged. No plugin replacement was required. Offline compilation is separate from the live evidence below.

## Live fixture and checks

The authorized disposable drawing was opened using the same startup/profile settings as the user's Civil 3D 2025 Hungary shortcut. Full filename and fingerprint were checked before writes. The initial drawing was writable and saved; an unchanged filesystem backup was verified by size and SHA-256 before writes and again at the end. Other open work drawings and source files were not edited.

The existing synthetic mixed-curve parent from the preceding validation was used. One named recipe call created two new offsets, -3.5 and +3.5, with `fullLength: false`, `startStation: 1200`, `endStation: 2600`, `lockToStations: true`, and host-managed `saveDrawing: true`.

**Proven:** both creation results reported `lockMode: "Station"` and `updateMode: "Dynamic"`. Independent native `OffsetAlignmentInfo` readback agreed. Both parent-end locking booleans were false, so this fixture used specified stations rather than the parent's start/end.

The parent's SCS PI was then moved with its actual native triangular PI grip in the Civil UI. Independent native tangent-definition readback confirmed the edit; the parent length changed from approximately 2152.9557562 to 2178.0687504 drawing units. Both offsets changed geometry automatically.

| Stage | Left projected parent range | Right projected parent range | Lock mode |
| --- | --- | --- | --- |
| Before grip edit | 1200..2600 | 1200..2600 | Station |
| After grip edit | 1200..2600 | 1200..2600 | Station |
| After host save | 1200..2600 | 1200..2600 | Station |
| After actual close/reopen | 1200..2600 | 1200..2600 | Station |

Range checks projected each child's actual endpoint coordinates onto the parent with native `StationOffset`; they did not confuse child stationing with parent stationing. At each stage, 13 interior points per offset (26 total) also retained signed offsets -3.5/+3.5 within `1e-8` drawing units. All new offset primitive endpoint/station gaps were below that tolerance.

One pre-save identity query returned `not_started` while the native grip interaction remained active; no save script executed. After exiting that interaction and checking idle health and drawing identity, the save succeeded. Alignment creation and the grip edit were not replayed.

## Persistence and scope

**Proven:** host saving produced `DBMOD=0` and native `Saved=true`. The actual document was closed and reopened from disk in the same Hungary-profile Civil process. All seven synthetic alignment handles and their saved geometry, definitions, parent links, modes, projected ranges, and samples matched the saved state within `1e-8`. The reopened drawing again had `DBMOD=0`, `Saved=true`, and idle plugin health. Original object-snapping settings were restored, and the backup remained unchanged.

**Unverified:** station locking when a requested range becomes invalid after parent edits, full-length station-lock behavior, and other Civil versions were not tested. The default Geometry-lock behavior remains covered by the [preceding report](alignment-recipes-f740b4c-2026-10-08.md). The distinction is consistent with Autodesk's [Offset Parameters documentation](https://help.autodesk.com/cloudhelp/2025/ENU/Civil3D-UserGuide/files/GUID-7EAC81A4-EF24-436E-AC0B-2139E5D7E9D8.htm).

Only this sanitized report is published; private paths, target identity, raw receipts, screenshots, and Autodesk binaries remain local.
