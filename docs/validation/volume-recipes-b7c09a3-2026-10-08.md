# Persistent volume surface and bounded volumes: Civil 3D 2025 validation

Tested implementation: `b7c09a3bc141e1c14b8d1588593f8b3ace991aa9`, branch `claude/kind-volta-rsvbm5`. Date: 2026-10-08.

**Proven:** both recipes ran by name in the user's existing Civil 3D 2025 Hungary instance on the explicitly authorized disposable test drawing. Creation, save/reopen persistence, native bounded-volume comparison, source-change detection, and rebuild/readback completed. `bounded_volumes` returns **unadjusted** quantities in this test. Curved boundaries require an explicit approximation tolerance when comparing with the native dashboard.

## Preparation and offline checks

The requested remote branch was fetched and fast-forwarded; the latest implementation commit was exactly `b7c09a3`. An isolated checkout of that commit was used for validation and this report-only publication.

| Check | Result |
| --- | --- |
| `npm ci` | Passed; dependency advisory noted below. |
| `npm run build` | Passed. |
| `npm run test:skills` | Passed: 32 Node tests, metadata compilation of all 34 C# templates against authentic installed Civil 3D 2025 assemblies, and 83 host-free validation/statistics cases (18 + 11 + 54). |
| `npm run test:mcp` | Passed: exactly three public MCP tools and 31 discoverable skills. |
| Updated local checkout | Requested feature branch, exact tested implementation; build and MCP smoke passed. |
| Fresh MCP server/live check | Fresh Node stdio servers used for the probes; a fresh server from the ordinary local checkout exposed both new named recipes and completed a normal query. The registered desktop MCP tools also read both new recipes successfully. |

This commit changes two recipe files, their documentation, and discovery-test expectations. Node implementation, plugin source, and dependency lockfile are unchanged from its parent. The running plugin is source-equivalent to the current implementation; no plugin binary replacement was required. Other desktop clients were not forcibly terminated.

Offline compilation and host-free tests are separate evidence from the live results below.

## Disposable target and persistence

**Proven:** full drawing filename and fingerprint were checked before writes. The initial drawing was writable and saved. An unchanged filesystem copy was made before the first write, and its size and SHA-256 were verified again after the test. Client/source drawings were not edited.

Both existing source surfaces in the test drawing were data-shortcut references. The comparison surface was pasted into a new local TIN surface for the source-edit trial. Triangle counts matched, and the local copy initially matched all 33 common covered sample points from a bounded grid scan. The original references remained the source of the baseline checks.

`civil3d_execute` with `skill: "create_tin_volume_surface"`, `cutFactor: 1.2`, `fillFactor: 0.9`, and `saveDrawing: true` created a local persistent TIN volume surface. Independent readback confirmed its source handles, factors, raw/adjusted summaries, and `DBMOD=0`.

The actual document was closed and reopened from disk in the same existing Hungary-profile instance. The same fingerprint, volume-surface handle, source handles, factors, and volume summaries were present afterward; the reopened document was saved with `DBMOD=0`. This proves document persistence, without claiming a complete application restart.

## Factor handling and full enclosing boundary

**Proven:** a closed rectangle enclosing the entire volume surface was queried by name through `bounded_volumes`. Its cut, fill, and net matched `UnadjustedCutVolume`, `UnadjustedFillVolume`, and `UnadjustedNetVolume` exactly, both before and after the source change/rebuild.

For this Civil 3D 2025 TIN volume surface:

```text
bounded cut/fill/net = raw quantities
adjusted cut = bounded cut * cutFactor
adjusted fill = bounded fill * fillFactor
adjusted net = adjusted fill - adjusted cut
```

The recipe's returned `cutFactor` and `fillFactor` are metadata; they are not already applied to its bounded quantities. The native parent-surface dashboard summary matched the adjusted API summary at the displayed precision.

The full enclosing rectangle intentionally overlaps the two smaller test boundaries. Each row was compared individually. The recipe's sum is a sum of rows, including overlaps, and was not treated as a deduplicated whole-surface quantity.

## Actual native Bounded Volumes comparison

**Proven:** the existing volume surface was added to the native Volumes Dashboard. Two actual closed lightweight polylines were independently picked through **Add Bounded Volume**: one straight rectangle, and one boundary with a nonzero-bulge arc. This was an actual UI comparison, not a second call to the recipe's API algorithm. Autodesk documents this workflow in [Adding Bounded Volumes](https://help.autodesk.com/cloudhelp/2025/ENU/Civil3D-UserGuide/files/GUID-040A33A4-BC45-460D-93B7-36413BF4AD60.htm).

| Comparison | Observed result |
| --- | --- |
| Straight boundary, native child factors 1.0 / 1.0 | Raw cut, fill, and signed net matched the recipe at the dashboard's 0.01-unit display precision. |
| Straight boundary, native child factors explicitly changed to 1.2 / 0.9 | Native adjusted quantities matched recipe raw quantities multiplied by the corresponding factors, before and after rebuild. |
| Arc boundary, native mid-ordinate distance 1.0 | About 0.81% cut difference from the recipe's 64-chord result. This is a material approximation-setting difference, not an exact-match pass. |
| Arc boundary, native mid-ordinate distance 0.001, recipe 16 chords | Cut differed by about 0.0153% from the native displayed result. |
| Arc boundary, native mid-ordinate distance 0.001, recipe 64 chords | Cut differed by less than 0.001% from the native displayed result. Convergence was observed; exact geometric equality was not asserted. |

Native bounded child rows defaulted to their own factors 1.0 / 1.0 in this run; they did not automatically inherit the parent volume surface's 1.2 / 0.9 factors. The native net column labels cut/fill direction, whereas the recipe returns signed net (`fill - cut`). Comparisons accounted for that sign convention.

The recipe uses a fixed chord count per arc; the native dashboard uses a mid-ordinate distance. The [Volumes Dashboard documentation](https://help.autodesk.com/cloudhelp/2025/ENU/Civil3D-UserGuide/files/GUID-7246FF86-2C5F-4B5A-991D-5571DAAC0CDF.htm) describes its adjusted columns, factors, and curve setting. Equal-looking parameter values do not imply equivalent tessellation.

The recipe's `planArea` is the area of its sampled boundary polygon. In this test the small boundaries partly extended outside the volume TIN; native 2D area represented the covered calculation area and was therefore smaller. Polygon area and covered surface area must not be equated. Small floating-point cancellation was also observed in polygon-area calculation at large drawing coordinates; this did not prevent the volume comparisons.

## Source edit, stale flag, and rebuild

**Proven:** automatic rebuilding was disabled for the local comparison and volume surfaces. The local comparison was raised by 0.1 drawing unit and rebuilt; neither referenced original source was edited.

Independent requests observed the sequence:

```text
before edit:                    isOutOfDate = false
after source edit/rebuild:      isOutOfDate = true; old volume totals remain
after explicit volume rebuild: isOutOfDate = false; new volume totals returned
```

The stale `bounded_volumes` response exposed `isOutOfDate: true` and retained the previous quantities. After explicit volume rebuild and host-managed save, the recipe returned changed quantities, matched the new raw whole-surface summary for the enclosing rectangle, and retained factors 1.2 / 0.9. The native dashboard also showed the changed parent and bounded-row quantities.

All 33 baseline covered sample points were independently reread: the local comparison changed by the intended offset within floating-point tolerance, while both original reference elevations were unchanged. The checked triangle counts were unchanged. This is sampled elevation evidence, not a claim of a complete point-by-point source-file audit.

During the source-edit sequence, an immediate follow-up saw `DBMOD=1` despite the successful host save response. The next write was stopped by the saved-state guard; actual state was reconciled and a further host-managed save established `DBMOD=0` while retaining the stale flag. The exact cause of that intermediate dirty flag is **Unverified**. It must not be used to infer a failed write or an automatic rollback. No source edit was repeated.

Final independent readback from a fresh local MCP server confirmed the expected drawing identity, Hungary profile, saved state, `DBMOD=0`, rebuilt volume, unchanged factors, a successful normal query, and idle plugin health with an empty queue.

## Scope and follow-up

**Proven:** the named recipes work for the tested Civil 3D 2025 TIN surfaces and closed lightweight polyline boundaries, including one curved boundary, in the authorized populated disposable model.

**Probable:** the observed arc difference is explained by the differing native/recipe tessellation settings; reducing native mid-ordinate distance and increasing recipe chord count demonstrated convergence.

**Unverified:** later Civil versions, Polyline3d boundaries, arbitrary arc radii/complex boundary shapes, generic performance, external data-shortcut source refresh, and complete application restart persistence. The fixed maximum of 64 chords is not a general accuracy guarantee for arbitrary curves.

The next documentation update can replace the recipes' pending-live-validation statements with this bounded evidence and explicitly state that bounded quantities are raw. No recipe implementation or validation metadata was changed in this report-only commit.

`npm ci` reported one high-severity SDK advisory, [GHSA-6qxp-vccf-f47h](https://github.com/advisories/GHSA-6qxp-vccf-f47h), concerning the SDK OAuth client. This validation used local stdio transport; it did not test OAuth. The requested implementation and lockfile were kept unchanged, so dependency remediation remains a separate follow-up.

Raw receipts, screenshots, source/drawing names, coordinates, paths, volume quantities, Autodesk binaries, and benchmark timings remain local. This report publishes test outcomes and normalized comparisons only.
