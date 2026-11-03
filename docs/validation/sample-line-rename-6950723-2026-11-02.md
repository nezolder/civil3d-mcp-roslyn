# Sample line rename live validation

Date: 2026-11-02 (Europe/Budapest). Tested recipe: `rename_sample_lines_by_station` at commit `6950723`. The live host was Civil 3D 2025 Hungary with the permanently installed plugin. The recipe is not in that host's installed catalogue yet, so its template was bound locally with the repository's `bindSkillCode` and sent as `code`; the only change was the bound parameter lines. In the `apply: true` run the final `lines` projection was filtered to non-`unchanged` rows to shorten the reply; the logic above the `return` was unchanged.

## Fixture

**Proven:** a separate Civil 3D 2025 process was started with the user's Hungary shortcut on a disposable `fixture.dwg`, copied from the stock Hungary metric NCS template. An unchanged backup was kept and verified by SHA-256. The user's other running Civil 3D instance was not closed, focused or sent any command; every call was routed to the test instance by `instanceId`.

Each write checked the full filename and fingerprint, ran with `saveDrawing: true`, and was followed by an independent read with `DBMOD=0`.

1. `create_alignment_from_pis` (by name): alignment `TEST_RENAME`, PIs `(0,0)`, `(200,0)` with R=100, `(300,100)`; tangent 0–158.58, arc 158.58–237.12, tangent 237.12–337.12.
2. `create_sample_lines` (by name): group `TEST_SLG`, 22 lines (20 m interval, geometry points, explicit `KSZ_A` at 50), widths 15/15.
3. Lines were moved by setting `SampleLine.Station`: `SL_040.00` to 43.30, `SL_060.00` to 80 and `SL_080.00` to 60 (a name swap), `KSZ_A` to 51. On the arc, `SL_220.00` had its left and right vertices shifted by -3 m in X.

## Rename

- **Preview** (`apply: false`, `onlyNamePrefix: "SL_"`, run through `civil3d_execute` without save): 3 renames (`SL_040.00` → `SL_043.30`, `SL_080.00` → `SL_060.00`, `SL_060.00` → `SL_080.00`), 18 unchanged, `KSZ_A` kept by prefix. All 21 selected stations were confirmed on the lines' own vertices. `DBMOD` stayed 0 afterwards.
- **Apply** (`apply: true`, `saveDrawing: true`): the same 3 lines renamed, including the swap through temporary names. Independent readback: 22 lines, every `SL_` name matches its station, no `TMP_RENAME_` name left, `DBMOD=0`, same fingerprint.
- **Reopen:** the test process was closed normally and the saved DWG reopened in a new Civil 3D 2025 Hungary process. A new client read the same 22 names and stations, `DBMOD=0`, same fingerprint. The test process was then closed normally; the saved file's SHA-256 was unchanged by the reopen and read.

## Civil API observations

- `TransformBy(Matrix3d.Displacement(...))` on a sample line returned without error but did not move it (`LockToStation=true`); `Station` and vertices stayed the same after commit.
- Setting `SampleLine.Station` moves a station-locked line; its vertices follow immediately.
- `SampleLineVertex.Location` cannot be set on the centre vertex (`InvalidOperationException`). Shifting only the left and right vertices kept the centre vertex and `Station` (220.00) unchanged, so the recipe left that line's name as it was, which matches Civil 3D's own station.
- After a script threw and its transaction was rolled back, that line's `LockToStation=false` (set before the throw) was still visible with `DBMOD=0`. The cause is unclear; it affected only the disposable fixture.

## Unverified

- A line whose station is *not* on its own vertices (the refusal path), the `{km}+{m}` format live (checked offline only), collision refusal live, station equations, and grip editing in the UI.

Raw local results, hashes and paths stay private.
