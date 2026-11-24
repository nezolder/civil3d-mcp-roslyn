# Live smoke test, first live runs (2026-11-24)

**Proven:** `npm run smoke:live -- --start-civil` at commit `e0b17ec` passed in Civil 3D 2025
(Hungarian install, `C3D_Hungary.arg` profile, stock `_Autodesk Civil 3D (Metric) NCS.dwt`) on
the owner's work PC: 33 cases passed, 2 warnings (both expected, below), 0 failed, in 25.9 s.
The installed plugin was the merged `23b0fe2`; the server came from the branch build.

Four runs were needed. Runs 1 to 3 found two Civil 3D behaviours that changed the runner. Each run
used its own new output folder under `%TEMP%`, started its own Civil 3D instances, and touched
no other instance or drawing.

## Runs

| Run | Commit | Result | What it showed |
| --- | --- | --- | --- |
| 1 | `bbd308e` | Stopped at F2 | The drawing had been named by SAVEAS in the startup script of the same session. The first saved write (F1) succeeded. The second (F2), 3 s later, failed with `CIVIL3D.SAVE_FAILED: eFileAccessErr`. An AutoCAD alert ("Error writing/closing file") blocked the instance for 77 s until it was clicked away. The journal shows `committed: true, saved: false`. |
| 2 | `276e42b` | Stopped at F5 | The fixture is now made by a first start (template, SAVEAS, QUIT) and opened from its file by a second start. F1 to F4 were saved cleanly. The F4 receipt said `dbmodAfter: 0`, but the next call, 42 ms later, found `DBMOD` 1, so its `requireClean` refused it. F4 had created a profile view. |
| 3 | `88835df` | Stopped at W3 | Fixture writes no longer ask for a clean drawing. Fixture, reads, preview and the real write passed (21 cases, the BasicLane corridor included). 59 ms after the saved real write, which changed the alignment that the profiles, profile view, sample lines and corridor depend on, `DBMOD` was 1 again. |
| 4 | `e0b17ec` | **Passed** | Case W2b reports the re-dirtied drawing as a warning. Settle steps (save, wait 1.5 s, read `DBMOD`, at most three rounds) run before the next `requireClean` write. Every settle needed one round. |

## Evidence from run 4

- Fixture: TIN surface, centerline polyline, alignment (3 elements), ground profile and profile
  view, design profile, sample lines every 20 units, and the assembly with the stock BasicLane
  on both sides (`ImportStockSubassembly(..., "Subassembly.BasicLane", ...)` plus
  `MirrorSubassembly`). The corridor was rebuilt and not out of date.
- Reads R1 to R9 matched the generated geometry. The surface elevation was exact on the plane.
  The design elevation at the start was 100.8. The station of the second vertex equalled the first
  leg's length, and its offset was 0.
- W1 preview: `preview: true`, `committed: false`, `rolledBack: true`; Line 1 added and Alignment 1
  modified were listed. Afterwards there was no line, the description was unchanged and `DBMOD` was 0.
- W2 real write: saved, with a backup copy present on disk; the new line was listed by its handle.
  The modified objects were Profile 2, Alignment 1, Corridor 1, ProfileView 1 and
  SampleLineGroup 1. A readback right after it showed `DBMOD` 1 (warning W2b).
- W3 failing script: `CIVIL3D.TRANSACTION_FAILED`, `reported_error`, `rolledBack: true`. Still one
  probe line, `DBMOD` 0.
- W4/W5: a write without save left `DBMOD` 1; the next `requireClean` call was refused with
  `CIVIL3D.DRAWING_NOT_CLEAN`, `not_started`.
- W6/W7: a save made the drawing clean. The same `idempotencyKey` sent again got
  `CIVIL3D.IDEMPOTENCY_COMPLETED`, `not_started`.
- W8: the corridor rebuild was saved; Alignment 1, Corridor 1 and Profile 1 were modified. The
  settle afterwards found the drawing clean (`DBMOD` 0→0).
- G1 wrong `expectedDrawing`: `CIVIL3D.DRAWING_MISMATCH`. G2 wrong session token:
  `CIVIL3D.UNAUTHORIZED`.
- E1: same drawing, `DBMOD` 0, 1 surface, 1 alignment, 1 corridor, 1 assembly.
- Journal of the run-4 instance: the `dbmodBefore` of F5, F6 and F7 was 1. After every saved
  write that touched objects with dependents, the drawing was marked modified again before
  the next call.

## Lessons

- Civil 3D can mark a drawing modified right after a saved write when it updates dependent
  objects (profile views, profiles, sample lines, corridors) after the command. A `requireClean`
  call straight after it is then refused, although the receipt said `dbmodAfter: 0`. The usage
  guide (writing topic) now says to save once more. A plugin-side remedy (saving after the
  command has ended) is not part of this change.
- A drawing named by SAVEAS earlier in the same session failed its second API save with
  `eFileAccessErr`. The cause was not looked into further. The runner avoids it by opening the
  file in a fresh start.
- AutoCAD's own alert during a failed save blocks the instance until someone clicks it.

## Not covered

- Other templates and profiles, Civil 3D 2026/2027, the `--port` attach mode, and the home PCs.
- The `C:\Program Files\Autodesk\AutoCAD 2025\acad.exe` default path was used as given. The
  default template search found the stock template only from run 4 on (the `_Autodesk ...` name).
