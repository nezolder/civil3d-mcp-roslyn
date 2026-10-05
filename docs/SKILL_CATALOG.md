# Recipe discovery and validation metadata

`civil3d_skills` keeps the same `list`, `search`, `get` and `api_lookup` actions and the same three-tool public interface. Catalogue `list`, `search` and `get` never execute a template or connect to a drawing; the separate `api_lookup` action still reads metadata through the connected Civil host. The C# recipe bodies, stable names, categories, parameters and write requirements remain unchanged.

## Find the required operation

Search matches every whitespace-separated word across the name, category, description, parameters, aliases and workflow tags. Word order does not matter. Case, accents, underscores and hyphens are normalized: `hossz-szelvény`, `hossz szelveny` and `HOSSZ–SZELVÉNY` find the same recipes. Category filtering and existing pagination still apply.

Examples include `hossz-szelvény`, `keresztszelvény`, `cross section`, `közmű` and `mennyiség`. A match identifies a possible operation, not a complete engineering method: the pipe-network recipe is gravity QC, the quantity recipes read existing results, and profile/view creation has specific input and validation limits.

Use a narrow search or category first, then `get` the chosen stable recipe name. Check inputs, write scope, prerequisites and detailed evidence before adapting its C# body. Missing catalogue coverage can require a separately developed operation; do not imply that a matching keyword supplies a network-design, hydraulic-sizing or complete drawing-production method.

## Response fields

Existing response fields and pagination fields remain. `search` additionally returns the existing write requirement so that inspection and authoring results are distinguishable before retrieving code.

| Added field | Meaning | Returned by |
| --- | --- | --- |
| `aliases` | Specific Hungarian/English task phrases used by the search index. | `get`; omitted from pages to keep discovery compact. |
| `workflow_tags` | A small set of task roles, such as `drawing_view`, `quantity_read` or `source_state`. | `list`, `search`, `get`. |
| `tested_civil_version` | Civil version covered by the recorded checks, including offline API compilation. It is not a live certification. | `list`, `search`, `get`. |
| `validation_summary` | Short scoped evidence and remaining limitations. Read the body for the full evidence. | `list`, `search`, `get`. |

Absent or invalid string evidence fields are `null`, meaning unknown; absent or invalid alias/tag arrays are empty. The loader does not infer live, saved or reopened success from an action name, the version, or a successful run. The read/write flag does not replace drawing guards, authorized scope, backups or independent saved-state checks.

The reviewed catalogue is compiled against Civil 3D 2025 API metadata. Some recipes have only an empty-fixture live check, some have a scoped data-bearing save/readback or reopen, and `create_surface_profile_view` has offline evidence while live creation/save/reopen remain unverified. These distinctions are intentionally visible before loading the full code.

This is an additive metadata extension. Consumers that require an exact response-key set must accept the additional fields; existing field values and pagination behavior are retained. The new Node module is used by a newly started MCP server. Existing processes retain their already-loaded module until the client reconnects/restarts that server; the Civil plugin does not need rebuilding or reinstallation for this change.

## Authoring the optional frontmatter

The existing lightweight parser is retained. New discovery fields use JSON-compatible inline YAML: string arrays and quoted single-line strings (or `null`). No new YAML dependency is installed.

```yaml
aliases: ["terep hossz-szelvény", "TIN surface profile view"]
workflow_tags: ["drawing_view", "modeling"]
tested_civil_version: "2025"
validation_summary: "Offline compilation checked. Live creation, save and reopen unverified."
```

Arrays are limited to eight nonempty strings of at most 100 characters. Version strings are limited to 40 characters and summaries to 280. Unicode control characters (category `Cc`), incorrect types or invalid JSON are treated as unknown/empty instead of creating a validation claim or dropping an otherwise valid legacy recipe. Keep authored aliases and summaries shorter than these limits and preserve the detailed evidence in the recipe body.
