# Survey row station candidates in dense junctions: live validation

Date: 2026-11-09 (Europe/Budapest). Tested recipe: `survey_row_station_candidates` at commit `06e7387`. The earlier validation (`survey-row-candidates-fd147bf-2026-11-03.md`) found the regular rows but missed rows in the junction area of the same test model; this run checks the changes made for that.

## Fixture and method

**Proven:** the same test model as on 2026-11-03 (a real road project prepared by the user; one centreline alignment of about 350 m, no station equations; the survey is a 2D survey DWG attached as an xref). Fresh copies of the drawing and of the survey xref were made in a temporary folder, with the xref at the same relative path; their SHA-256 matched the originals. The originals were never opened.

The live host was Civil 3D 2025 Hungary on the user's second PC, with the plugin installed. Its installed catalogue does not contain this version of the recipe, so the template from commit `06e7387` was sent as `code` through `civil3d_query`, with only the parameter lines bound and three names changed: the copies were read into a side database (`Database.ReadDwgFile`, xrefs resolved, read-only file share), so `Database`, `Transaction` and `CivilDoc` pointed to that side database, its transaction and its Civil document. Nothing was written; the active drawing in the host (an empty template) was not touched.

## Reference rows

The survey blocks carry the surveyor's description per point. Along this alignment the surveyor measured 14 cross rows, about every 25 m, each with the pavement edge, shoulder, ditch edges and ditch bottom on both sides and usually the lane marking on the centreline: 2395, 2421.7, 2446.8, 2472, 2496.4, 2520.9, 2546.6, 2570.9, about 2597, about 2621, 2645.8, about 2672, about 2696 and 2720.5. Four of them lie in the junction area between 2585 and 2700:

- about 2597: a gravel side road joins on the right; its edge points fill the row's surroundings.
- about 2621: the right half was measured at 2619, the left half at 2622 (about 3 m apart, more than `rowGap`).
- about 2672: ditch start and end points around it.
- about 2696: the right half at 2694.5, the left half at 2696.8, with field edge points, a fence, trees and a pole that chained the group to 6 m.

## Runs

All runs read 13,843 survey points and blocks from the xref; 438 lay within the alignment and `maxOffset` 50 m (196 with the attribute filter). No projection mismatch.

| Settings | Candidates | Reference rows found | Extra candidates | `needsReview` |
|---|---|---|---|---|
| Before (commit `fd147bf`, from the earlier report): `minPoints` 6, `requireCrossing` | 14 | 10 of 14 (missed 2597, 2621, 2672, 2696) | 4 | n/a |
| `06e7387`, `minPoints` 6, `requireCrossing`, defaults otherwise | 24 | 14 of 14 | 10 | 12: all 10 extras, plus the two staggered rows (2620.74, halves 3.1 m apart; 2695.80, 2.4 m apart) |
| `06e7387`, `minPoints` 4, `requireCrossing`, `attributeFilter` on the row features (pavement edge, shoulder, ditch, lane marking, side road joint), `attributeTags` the description tag | 16 | 14 of 14 | 2 | 5 |

- In the second run the 12 unflagged candidates were all reference rows. Four candidates came from cut groups (`origin` "split") and six from joined halves ("joined"). The extras had 0 to 5 matching points, the reference rows 8 to 16.
- The 2621 row came out at 2620.74 (`leftStation` about 2621.9, `rightStation` about 2618.8) and the 2696 row at 2695.80; both were flagged because one perpendicular sample line misses one half by 2 to 3 m.
- `sources.attributesSeen` listed the code and description tags first and the point number and height tags (2000+ distinct values) after them.
- The attribute-filter run used the template just before the commit; the only difference to `06e7387` is the order of `attributesSeen`. The other runs used `06e7387` itself.

## Host-free tests

`EngineeringSkillInputTests` passed 84 cases, including new ones for cutting a long group, turning it off, joining halves, turning it off, not joining a group that is a row on its own, the centre tolerance, matching points and the attribute filters. The Node test suite passed (90 tests). The full metadata compilation against the Civil 3D 2025 reference DLLs (`npm run test:skills`) was not run in this environment; the live runs above compiled and ran the full template in Civil 3D 2025.

## Unverified

- The named run (`skill` + `params`) through an installed catalogue with this version.
- COGO point descriptions as attribute filter input, point groups, nested or rotated xrefs.
- Other surveys and junction layouts; the review thresholds (half of the upper quartile of matching points, 2 × `rowGap` for halves) come from this one survey.
- Creating sample lines from the new `sampleLineStations`.

Raw local results, hashes and paths stay private.
