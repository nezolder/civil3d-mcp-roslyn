# Reusable engineering recipes

Six focused recipes reuse the existing dynamic C# execution path. Retrieve them with `civil3d_skills`, bind the explicit inputs, then use `civil3d_query` for reads or `civil3d_execute` for approved writes. The public interface remains exactly three tools; the Civil plugin is unchanged.

The [catalogue contract](SKILL_CATALOG.md) explains task aliases, workflow tags, and the short version/validation fields returned before loading code. A version or keyword match does not expand the scoped evidence below.

| Work problem | Recipe | Inputs and scope |
| --- | --- | --- |
| Create a design profile from approved breakpoints | `create_design_profile_from_pvis` | One local alignment, explicit PVI stations/elevations/symmetric curve lengths, named existing styles. Creates a new profile. |
| Place individual cross-section views | `create_section_views` | One existing sampled group, station window, named view/band styles, origin and grid gaps. Creates at most 50 views; existing views refuse the selected batch. |
| Read Civil's material quantities | `material_quantity_report` | Exact sample-line group, existing material-list GUID and station window. Reads native cumulative/incremental results; does not calculate or replace material lists. |
| Compare elevations at specified XY locations | `compare_surface_elevations` | Two TIN handles, at most 2000 explicit points and a tolerance. Reports signed B−A statistics, coverage and missing/error counts. |
| Inspect data-reference state | `data_reference_audit` | One category or all seven declared categories. Reports reference problems and unknown/unscanned counts separately from capped detail rows. |
| Inspect corridor target assignments | `corridor_target_audit` | One corridor handle, optional exact baseline/region names. Reports assignments and potential self-surface dependencies within the declared scan scope. |

## Routine use

1. Identify the drawing and the necessary objects with the existing inventory recipes. Search/get the recipe above; reuse its validated body instead of writing a new implementation.
2. Supply project-approved values. The recipes do not invent design criteria, a vertical datum, material mapping, style names or allowable tolerances.
3. For a write, verify the full `Database.Filename`, `FingerprintGuid` and `DBMOD=0`; make an unchanged timestamped filesystem backup. Use the matching `expectedDrawing`, the selected instance and `saveDrawing: true`.
4. Independently verify returned objects, requested engineering values and the saved state. Reconcile a timeout before any further write. For views, inspect bands, clipping and readability visually before delivery.

Detail caps do not mean a full list was checked. Surface statistics include every supplied valid point, even when details are capped. Reference-state scans and corridor target-object scans explicitly report incomplete coverage. A material row represents the interval ending at its station; summing selected rows does not prove an exactly clipped volume at arbitrary range boundaries.

## Evidence as of 2026-10-01

- **Proven offline:** all six recipes compile against Civil 3D 2025 metadata. Host-free validation and mathematical cases exercise refused inputs, bounds and surface statistics. MCP list/search/get checks verify parameter metadata and write classification.
- **Proven in an isolated Civil 3D 2025 synthetic fixture:** three-PVI profile authoring/save/reopen; three styled section views with complete saved-state readback and no grid overlaps; TIN comparison with a known +1 difference and out-of-surface coverage; a native three-section material list with an independently expected fill total. The four read recipes retained `DBMOD=0` in their targeted probes.
- **Proven only on local/empty objects:** reference-audit category/cap/completeness reporting and an empty corridor with one baseline.
- **Unverified:** actual data-bearing DREF states, populated corridor target warnings, broader design-profile combinations, populated section-view bands, section-view saved-file reopen/print quality, model freshness and road-standard compliance. These boundaries are also recorded beside each recipe.

Creation can leave a subsequent native dirty flag after the initial save in the tested Civil configuration. The test reconciled actual objects, used a geometry-neutral host-managed save and independently confirmed `DBMOD=0`; it did not repeat successful creation or change the runtime's save contract.

## Upstream ideas and licenses

The recipes were independently written using Civil 3D 2025 API members. They adopt bounded workflow ideas, not upstream code bodies or fixed-tool frameworks.

| Source reviewed | License | Idea used |
| --- | --- | --- |
| [Mmpasta00/civil3d-mcp, 618fe787](https://github.com/Mmpasta00/civil3d-mcp/tree/618fe787ca414f551e359eac02cc9fe6c6af61ad) | MIT | PVI/profile and section-view recipes |
| [Jjo37/new-acad, 2085394b](https://github.com/Jjo37/new-acad/tree/2085394be8dc15a885d97bc8572efc5745441290) | MIT | Section workflows and target/reference audits |
| [Joshua8-AI/Civil3D-mcp, 8d1d1924](https://github.com/Joshua8-AI/Civil3D-mcp/tree/8d1d19249b4245957330acd8af1af90d27b9c0a9) | MIT | Read-only data-reference and corridor investigation |
| [7pka111223-jpg/C3D, 6b821fe6](https://github.com/7pka111223-jpg/C3D/tree/6b821fe627728c45547f37fc9eb2503c6b5bb255) | Apache-2.0 | Existing native QTO reporting |
| [xuantinhnbs-rgb/civil3d-mcp, ca2f6a33](https://github.com/xuantinhnbs-rgb/civil3d-mcp/tree/ca2f6a336e80e2745a217c77eed8f768378e751b) | MIT | Sampled surface comparison with coverage/error reporting |
| [JhulVF/Civil3d-mcp-tcce, 84ded7f](https://github.com/JhulVF/civil3d-mcp-tcce/tree/84ded7f) | MIT | Volume surfaces with cut/fill factors and per-boundary volumes (`create_tin_volume_surface`, `bounded_volumes`); alignment layout from PIs and offset alignments (`create_alignment_from_pis`, `create_offset_alignments`) |

Future copying of source code needs a separate license/notice review. No source from repositories without a clear license is included. PKT manipulation and automatic target/DREF repair remain outside this phase.
