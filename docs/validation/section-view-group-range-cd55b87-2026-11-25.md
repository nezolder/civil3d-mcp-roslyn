# Section view group station range — live check (cd55b87, 2026-11-25)

Civil 3D 2025, generated live smoke drawing (`npm run smoke:live -- --start-civil`), installed plugin
ed879fd, server built from a worktree at cd55b87 (only the recipe changed). Smoke run: 34 passed,
2 warnings expected for that plugin (S1b version mismatch, W2b re-marking fixed in a later plugin),
0 failed. Sample lines every 20 units from 0 to 360, plus one at the alignment end, 370.39.

## `SectionViewGroups.Add` directly (preview, nothing kept, DBMOD 0 before and after)

New `SectionViewGroupCreationRangeOptions(group.ObjectId)`, default placement.

| Range passed | Views | Result |
| --- | --- | --- |
| exactly 100 to 200 | 5 (120 to 200) | line at 100 left out; line at 200 kept |
| 99.99 to 200.01 | 6 (100 to 200) | both end lines kept |
| exactly 0 to 370.39 (alignment start and end) | 20 | both end lines kept |
| -0.01 to 370.40 | — | throws "startStation should be larger than start station of Alignment" |

## Recipe `create_section_view_group` by name (cd55b87, saved into the test drawing)

| Parameters | Views | `stationRange.passedToCivil` |
| --- | --- | --- |
| startStation 100, endStation 200 | 6: 100, 120, 140, 160, 180, 200 | 99.99 to 200.01 |
| defaults (all lines) | 20: 0 to 370.39 | 0 to 370.39 (clamped to the alignment) |

Both receipts: committed, saved, clean after the save, backup made; the drawing was still clean
3 s later. An independent read of the drawing found 2 section view groups with the same view counts
and stations.

## Not covered

- Template (production) placement with the padded range.
- View name suffixes, for example `(32)`, kept counting through the preview runs that were rolled
  back; only the number at the end of the name is affected.
