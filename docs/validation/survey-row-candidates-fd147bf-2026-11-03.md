# Survey row station candidates live validation

Date: 2026-11-03 (Europe/Budapest). Tested recipe: `survey_row_station_candidates` at commit `fd147bf` (template unchanged since `6ad93d7`). The live host was Civil 3D 2025 Hungary with the permanently installed plugin. The recipe is not in that host's installed catalogue yet, so its template was bound locally with the repository's `bindSkillCode` and sent as `code` through `civil3d_query`; the only change was the bound parameter lines.

## Fixture

**Proven:** a separate Civil 3D 2025 process was started with the user's Hungary shortcut on a disposable copy of a test model the user prepared from a real road project. An unchanged backup was kept, and the copy's SHA-256 matched the original. The survey sits in a 2D survey DWG attached as an xref with a relative path; that xref was copied read-only next to the test folder so the relative path resolved. The user's and Codex's other running Civil 3D instances were not closed, focused or sent any command; every call was routed to the test instance by `instanceId` and checked the full filename and fingerprint.

The test drawing holds one centreline alignment (about 350 m, no station equations), a TIN surface and the survey xref. It has no COGO points and no points or blocks of its own: all survey points are inside the xref, as AutoCAD points on a survey point layer and a point block (`Mért_pontok`) inserted on each measured point, plus symbol blocks (trees, utilities, railway objects).

## Runs

Both runs read 25,585 objects from model space and the xref, found 3,277 AutoCAD points and 10,566 blocks (13,843 from the xref), and counted 1,744 block/point pairs on the same spot once. 12,604 points lay outside the alignment's station range and 801 beyond `maxOffset` 50 m; 438 were used. No projection mismatch.

- **Defaults** (`minPoints` 3, `requireCrossing` false): 88 groups, 35 candidates, 3 groups rejected for station spread above 5 m (a junction area), 50 groups with fewer than 3 points. Besides the full rows the list also held small one-sided groups of 3 to 7 points (for example a few points near the edge).
- **Stricter** (`minPoints` 6, `requireCrossing` true): 14 candidates. The first eight are the surveyor's cross-section rows about every 25 m (2395.07, 2421.74, 2446.77, 2471.95, 2496.37, 2520.94, 2546.63, 2569.63), each with 14 to 22 points on both sides, mostly `Mért_pontok` blocks, from the pavement edges and lane lines out to the shoulders. Rows had a station spread of 1.2 to 4.2 m, a straightness RMS of 0.3 to 1.4 m and a skew under 4 degrees, so the hand-measured rows are not straight lines; the fitted crossing station sat within the row's own station range in every case.
- `blockNamesSeen` and `layersSeen` listed the xref's names with their `xref|` prefix, as documented, so a first run shows the survey point block and layer to filter on.
- `DBMOD` stayed 0 after both runs. The test process was closed without a save prompt, and the test copy and the xref copy kept their SHA-256.

## Unverified

- Whether every candidate beyond 2570 is a real survey row (junction area with denser measurements); the user should check them in plan.
- Name filters on real data, COGO point input, point groups, nested xrefs, rotated or scaled xref insertion, dynamic block names, and station equations.
- Creating sample lines from `sampleLineStations` on this drawing.

Raw local results, hashes and paths stay private.
