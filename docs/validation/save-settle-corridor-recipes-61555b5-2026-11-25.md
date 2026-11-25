# Save until clean, corridor read recipes and API notes: live run (2026-11-25)

PR #35. Civil 3D 2025 (Hungarian profile) on the work PC, a fresh instance started by the live smoke
runner on its own generated drawing (`mcp_smoke.dwg` in a temporary folder). No work drawing, copy of
one or project source was opened.

## Builds

- Plugin and server built in a separate worktree at **61555b5**; the candidate plugin was installed
  with `scripts/install.mjs --plugin` (backup of the installed bundle taken first). Installed DLL
  before: `D5749125…78BF`; candidate: `27236139…b97e`.
- The recipe fixes **f539076** and **f4685f4** change only `skills/`; for them only the server was
  rebuilt, the candidate plugin stayed loaded.
- Afterwards the previous bundle was restored from the backup; all 9 files are byte-identical to the
  state before the test (DLL `D5749125…78BF` again).

## Live smoke test (61555b5)

`npm run smoke:live -- --start-civil`: **36 passed, 0 warnings, 0 failed**.

- `W2` (saved write to the alignment with profiles, profile view, sample lines and corridor): the
  receipt's `saveSettle` shows the drawing clean right after the first save, marked modified again
  by Civil 3D about a second later, one extra save by the plugin, clean at the second check.
- `W2b` (still clean right after that write) passed; in every earlier live run it was a warning.
- The runner's own settle steps `W2c`, `W7b` and `W9` found the drawing clean in their first round.

## Extra cases on the smoke drawing

- Saved write changing the alignment description: receipt `saved: true`, `dbmodAfter: 0`, one extra
  save; `DBMOD` 0 again 3 s later; the description read back as written.
- `apiNotes`: a query summing `GetBoundedVolumes` of the TIN surface over a small square returned the
  note `bounded-volumes-raw`; a plain `return 1;` query returned none.
- Recipes, `DBMOD` 0 before and after every read:
  - `assembly_inventory` at 61555b5 failed with its default inputs (result deeper than 8); fixed in
    f539076: one assembly, two BasicLane subassemblies (right and left) with 5 parameters each (Width
    3.6, Depth 0.2, Slope -0.02, ...), used by one corridor region.
  - `corridor_region_inventory`: one region 0 to 370.39, 21 built stations, largest spacing 25, no gap
    or overlap finding.
  - `corridor_applied_section` at 61555b5 stopped at station 50 on `AdjustedElevation` ("Cannot get
    adjusted elevation"); fixed in f539076 (optional, `null`). Stations 40 and 41.3 answer that there
    is no built station and name 25 and 50 (the corridor is built every 25). In f4685f4 at station
    50: crown point elevation 101.7179 = design profile elevation at 50 (read with
    `profile_elevation_at_station`), `elevationToBaseline` 0; edge at 3.6 m 101.6459 / -0.072 (-2 %);
    subbase points -0.2 and -0.272; one shape of 0.72 m²; links from 101.7179 to 101.6459.

## Not covered

- Re-marking that comes much later (section views after about 30 s) and a drawing that never becomes
  clean (`extraSaveLimitReached`) were not seen live; the stop reasons are tested offline only.
- Assemblies from Subassembly Composer, several baselines or regions, targets and offset baselines
  were not in the smoke drawing.
- Notes other than `bounded-volumes-raw` were not triggered live.
