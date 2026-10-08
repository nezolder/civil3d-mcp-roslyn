# Alignment recipes and SDK update: Civil 3D 2025 validation

Tested implementation: `f740b4cc0b4d56b3274f0227735bb8decd77bf34`, branch `claude/kind-volta-rsvbm5`. Date: 2026-10-08. This includes `93bf47e` (MCP SDK 1.31.0) and `f740b4c` (two alignment recipes).

**Proven:** both new recipes ran by name in the user's existing Civil 3D 2025 Hungary instance on the explicitly authorized disposable drawing. The mixed PI alignment, dynamic full/partial offsets, native PI grip edit, and actual save/close/reopen checks passed. A deliberately oversized radius was rejected with the affected PI pair identified and no residual alignment.

**Finding:** partial offsets initially use the requested parent station range, but the native default is **Geometry** locking. After the parent PI edit, their end station moved with the geometry. They did not retain a fixed numeric end station. Existing desktop MCP clients still require reconnect/restart; fresh updated servers were verified below.

## Source, dependency, and offline checks

The requested branch was fetched, checked out, and fast-forwarded. `git log -1` was exactly `f740b4c` before testing. Installation and tests used that implementation in the ordinary local checkout. This report is published separately from an isolated checkout of the same commit.

| Check | Result |
| --- | --- |
| `npm ci` | Passed. Installed SDK version independently read back as `1.31.0`. |
| `npm audit` | Passed: 0 known vulnerabilities at all severities. |
| `npm run build` | Passed. |
| `npm run test:skills` | Passed: 32 Node tests; all 36 C# skill templates compiled against authentic installed Civil 3D 2025 assemblies; 83 host-free validation/statistics cases passed (18 + 11 + 54). |
| `npm run test:mcp` | Passed: exactly three public MCP tools and 33 discoverable skills. |
| Fresh MCP server | Newly started stdio servers from the updated local build performed the live probes. The final fresh server exposed three tools, 33 skills, and both new named recipes; `return 1;` succeeded and health returned idle with an empty queue. |

Plugin and Node implementation sources are unchanged by these two commits. The existing accepted Civil plugin was used; a plugin binary replacement was unnecessary. Offline compilation and host-free tests do not replace the live evidence below.

## Target protection and test fixture

**Proven:** the full drawing filename and fingerprint were checked before writes. The initial target was writable and saved. An unchanged filesystem backup was made before the first alignment write. Its size and SHA-256 still matched at the end. Other open drawings and original source files were not edited.

All new alignment geometry was synthetic, separate from the existing engineering model. The starting drawing had no alignments. Every recipe write used `saveDrawing: true`; saving remained the host's responsibility.

## `create_alignment_from_pis`

The named recipe received five synthetic PI rows and `startStation: 1000`:

| PI | X | Y | Radius | Spiral in | Spiral out |
| --- | ---: | ---: | ---: | ---: | ---: |
| 0 | 10000 | 10000 | 0 | 0 | 0 |
| 1 | 10500 | 10000 | 120 | 0 | 0 |
| 2 | 10900 | 10400 | 200 | 60 | 50 |
| 3 | 11400 | 10400 | 0 | 0 | 0 |
| 4 | 11800 | 10800 | 0 | 0 | 0 |

**Proven:** independent queries of real native Civil objects found six top-level entities and eight connected primitives: four fixed lines, a free simple arc, and a free spiral-curve-spiral entity. The arc radii were 120 and 200, the asymmetric clothoid lengths were 60 and 50, and PI 3 remained a radius-zero corner between connected lines. Starting station was 1000. Primitive endpoint and station gaps were below `1e-8` drawing/station units.

The SCS PI was then moved with its actual native triangular PI grip in the Civil UI. This changed the two adjacent fixed tangent definitions, both curve shapes, and parent length. Both arc radii, both clothoid lengths, starting station, and the free curve constraints remained unchanged. All parent primitives remained connected. Native Properties also displayed reference/starting station `1+000.00` after the edit. The temporarily disabled object snapping was restored to its original setting.

This verifies real native grip editing rather than an API edit standing in for a grip. Shape and constraint readback came from independent queries in the same live Civil process.

### Oversized radius

A separate named request used the same fixture with radius 10000 at PI 1. It returned `success: false` with:

```text
Segment PI 0 - PI 1 is too short for its curves; reduce the radius or spiral lengths.
```

**Proven:** the failing alignment name was absent afterward, and the total alignment count stayed at five (the successful parent and four offsets). This case failed the recipe's **pre-write fit validation**. It does not prove rollback for every possible exception during native alignment construction.

## `create_offset_alignments`

The named recipe was run twice against the new parent: one pair at -3.5/+3.5 with full length, then another pair with `fullLength: false`, `startStation: 1200`, and `endStation: 2600`.

**Proven:** all four results were native offset alignments linked to the successful parent, with signed nominal offsets -3.5/+3.5 and `UpdateMode: Dynamic`. Before the grip edit, projected endpoints covered the full parent or precisely parent stations 1200..2600, respectively. A child's own stationing differs from parent stationing along a curved offset, so range verification projected its endpoint coordinates back onto the parent.

After the native parent PI edit, all four offsets changed automatically. For each offset, 13 interior points were computed from the child's native `PointLocation` and independently projected through the parent's `StationOffset`: 52 samples per stage, before the edit, after the edit, after saving, and after reopening. Signed offset errors stayed below `1e-8` drawing units. Full-length offsets continued to span the changed parent endpoints.

### Partial-range behavior

The native readback was `LockMode: Geometry`, with `LockToStartStation: false` and `LockToEndStation: false` for these results. The partial offsets initially projected to 1200..2600. After the PI edit, they projected to approximately **1200..2635.8446993**, retaining the geometric attachment rather than a fixed numeric end station.

**Proven:** dynamic following and the initial input range work for this fixture. **Probable:** the later range change is the expected native Geometry-lock behavior, consistent with Autodesk's distinction between geometry and station locking in [Offset Parameters](https://help.autodesk.com/cloudhelp/2025/ENU/Civil3D-UserGuide/files/GUID-7EAC81A4-EF24-436E-AC0B-2139E5D7E9D8.htm).

If consumers require the same numeric start/end stations after parent edits, that expectation needs an explicit station-locking option or contract clarification. This validation report does not change the recipe's locking behavior.

## Save, close, reopen, and final state

**Proven:** after the native grip edit, the host saved the drawing. Independent readback confirmed `DBMOD=0` and the native document's `Saved=true`. The actual document was then closed and reopened from disk in the same existing Hungary-profile Civil instance.

After reopening, all five alignment handles, definitions, geometry, curve parameters, native constraints, parent links, offset modes, ranges, and sampled offsets matched the saved state within `1e-8` numeric tolerance. Comparison was keyed by object handle because native enumeration order changed on reopening. The rejected alignment remained absent. The reopened drawing again had `DBMOD=0`, `Saved=true`, and idle plugin health. The backup remained unchanged.

## Limits and remaining client action

**Unverified:** arbitrary PI layouts, tight spiral fitting, native construction exceptions after writes begin, design-criteria compliance, and other Civil versions are outside this fixture. A normal document close/reopen was tested; a complete Civil application restart was not required or performed.

Fresh updated MCP servers were started and checked successfully. Older Node MCP processes owned by already-running Codex/Claude desktop clients were still present; those clients were not forcibly terminated. **Remaining action:** reconnect/restart those MCP clients so their persistent servers load SDK 1.31.0. This report does not claim that every existing desktop connection has already restarted.

Only this sanitized report is published. Private target identity, paths, client model data, raw receipts, screenshots, and installed Autodesk binaries remain local.
