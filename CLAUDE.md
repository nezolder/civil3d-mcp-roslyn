# CLAUDE.md

Notes for an AI coding assistant working on this repository. They say where things are and which
rules are easy to break. The details live in the linked documents; do not copy them here.

## What this is

An MCP server that lets an assistant drive Autodesk Civil 3D 2025 by sending C# code:

```
assistant --stdio--> Node MCP server (src/) --TCP JSON-RPC--> Civil 3D plugin (plugin/)
                                                               Roslyn compiles the script and runs it
                                                               on Civil 3D's main thread in a transaction
```

- Exactly three public tools: `civil3d_query` (read; its transaction is never committed),
  `civil3d_execute` (write; optional preview, clean check, backup, save, receipt) and
  `civil3d_skills` (recipe catalog, `api_lookup`, `status`, `guide`). Do not add tools; new
  capability goes into recipes.
- Recipes ("skills") are reviewed C# templates in `skills/<category>/<name>.skill.md`, run by name
  with bound parameters or fetched and adapted.
- This repository is public. Keep it to Civil 3D control: no client or project data, no
  office-specific standards or style names, no personal paths, computer names or network shares.
  Repository text is English.

## Layout

| Path | What |
| --- | --- |
| `src/` | Node/TypeScript MCP server: tool registration (`src/tools/`), instance discovery and transport (`src/utils/`), structured errors and hints (`src/errors/`), guide loader (`src/usageGuide.ts`), benchmark (`src/benchmark/`) |
| `plugin/Civil3dMcpPlugin/` | C# plugin (net8.0-windows): RPC server, `RoslynExecutor`, `ScriptSandbox`, `DrawingGuard`, `DrawingBackup`, `ChangeList`, `WriteJournal`, `SessionAuth`, `ResultSerializer` |
| `skills/` | Recipe files by category |
| `docs/USAGE.md` | The working order for assistants, served by the running server (see below) |
| `docs/SKILL_CATALOG.md` | Recipe metadata, search, run-by-name rules |
| `docs/ENGINEERING_SKILLS.md`, `docs/LIVE_SMOKE.md` | Engineering recipes; one-command live smoke test |
| `docs/validation/` | Records of live tests, named `<topic>-<commit>-<date>.md` |
| `tests/` | `errors`, `transport`, `plugin`, `roslyn`, `skills`, `benchmark`, `live-smoke`, `packaging` |
| `scripts/` | Build helpers, `install.mjs` (server and plugin install), `live-smoke.mjs`, `package-connector.mjs` (connector plugin packaging), `check-codex-install.mjs` (installs the Codex plugin with a real Codex CLI, reads it back and checks its approval rules in real calls) |
| `packaging/claude-plugin/`, `packaging/codex-plugin/` | The connector plugin `nezolder-civil3d-mcp` for Claude and Codex (launcher only, no skills; the launcher lives in `claude-plugin/`). Keep the name and the server name `civil3d_roslyn`; bump `version` in both manifests to release it (`.github/workflows/connector-release.yml`) |
| `C_References/` | Autodesk reference DLLs, never committed (see its README) |
| `PROJECT_STATUS.md` | Dated change log with evidence boundaries; update it with each feature |

## Build and checks

The checks expected before a pull request are listed in [CONTRIBUTING.md](CONTRIBUTING.md). In
short: `npm ci`, then

```bash
node --test tests/errors/*.test.mjs tests/skills/*.test.mjs tests/transport/*.test.mjs tests/plugin/*.test.mjs tests/benchmark/benchmark.test.mjs
npm run test:skills    # builds, then compiles every recipe against the Civil 3D 2025 DLLs
npm run test:mcp
npm run test:smoke     # live smoke runner against a simulated Civil 3D
npm run test:packaging # connector plugin: files, privacy check, marketplace update, launcher
```

`npm run build` must run before the Node tests (the `test:*` scripts do it themselves).

**On Linux (cloud containers)** there is no Civil 3D and no Autodesk install:

- .NET: `apt-get install -y dotnet-sdk-8.0`.
- Reference DLLs from NuGet: download
  `https://api.nuget.org/v3-flatcontainer/<id>/<version>/<id>.<version>.nupkg` (lower-case id),
  unzip, take `lib/net8.0/`, and copy into `C_References/` under these names:
  `autocad.net` 25.0.1 → `acmgd.dll` (from `AcMgd.dll`), `autocad.net.core` 25.0.0 →
  `accoremgd.dll` (from `AcCoreMgd.dll`), `autocad.net.model` 25.0.0 → `AcDbMgd.dll`,
  `civil3d.net` 13.7.1175 → `AecBaseMgd.dll`, `AeccDbMgd.dll`.
- Plugin build: `dotnet build plugin/Civil3dMcpPlugin -c Release -p:EnableWindowsTargeting=true`.
- Everything above passes on Linux. `npm run test:plugin` (plugin core tests) and two tests in
  `tests/benchmark/read-only-live-runner.test.mjs` assume Windows and fail on Linux, on `main`
  too. CI (`.github/workflows/ci.yml`, Windows, regional setting hu-HU) runs them on every pull
  request; `npm run test:skills` (.NET part) needs the DLLs and is not in CI.
- The Autodesk assemblies are mixed-mode and cannot be loaded on Linux: compile, do not run.
- One code body can be compile-checked alone:
  `dotnet run --project tests/skills/Civil3dMcp.SkillTests.csproj --configuration Release -- --code <file.cs>`.

Text built from numbers must use `CultureInfo.InvariantCulture`: the plugin runs on Windows set to
a decimal comma, and CI tests under that setting.

## Writing recipes

The format and run-by-name rules are in [docs/SKILL_CATALOG.md](docs/SKILL_CATALOG.md); copy an
existing recipe of the same category. Easy to miss:

- Frontmatter: `name`, `category`, `description`, `requires_write`, `aliases` (at least 2),
  `workflow_tags`, `tested_civil_version: "2025"`, `validation_summary` (at most 280 characters,
  only what was proven), `parameters`.
- Run by name: each parameter is declared exactly once at the start of a top-level line with a
  literal default (`var x = "..."`, `0.0`, `double.NaN`, `true`, `16`, `new (double a, string b)[] { }`).
  A one-element tuple is impossible; use pairs.
- Write `Autodesk.Civil.DatabaseServices.Subassembly` in full (the short name fails once a corridor
  drawing is open), and the full name of any type that exists in both AutoCAD and Civil 3D
  namespaces other than the defaults listed in README "Script Globals".
- Scripts are synchronous (`await` is refused) and may not return `DBObject`s.
- A read-only recipe must not create anything, not even temporarily: a rolled-back query can still
  leave the drawing marked modified. `SampleLineGroup.GetSectionSources()` needs write access and
  is refused in queries.
- Office habits (template paths, style names, layer names, distances) are parameters, never
  hard-coded values.
- Where Civil 3D keeps an object dynamic (offset alignments following their parent, volume
  surfaces), keep it dynamic rather than baking geometry.
- After adding or removing a recipe, update the counts in `README.md` and `PROJECT_STATUS.md` in
  their fixed wording; `tests/skills/documented-counts.test.mjs` fails until they match.

Civil 3D 2025 API lessons are kept in the recipes that hit them (for example section view groups,
offset alignments, bounded volumes); read the related recipe before writing a new one in the same
area, and use `civil3d_skills` `api_lookup` or the compile check instead of guessing member names.

## The usage guide

`docs/USAGE.md` is the one place for the working order an assistant follows (status, identifying
the drawing, write inputs, receipt, readback, errors). The build copies it next to the server; its
`core` part is sent as the MCP `instructions`, the other parts by topic (`<!-- topic: name -->`
markers). Size limits are enforced by `tests/skills/usage-guide.test.mjs`: `core` at most 2048
bytes, each topic at most 6144. Several topics are close to their limit; trim before adding. Do not
restate the working order in other files; link to it.

## Live tests and write safety

Offline tests and a successful compile are not evidence of Civil 3D behaviour. Live rules
(also in [CONTRIBUTING.md](CONTRIBUTING.md)):

- Live tests run only on a throwaway copy of a drawing or on the generated drawing of the live
  smoke test ([docs/LIVE_SMOKE.md](docs/LIVE_SMOKE.md), `npm run smoke:live -- --start-civil`).
  Never write to a work drawing or its sources (xrefs, data shortcuts, survey files).
- Writes follow USAGE.md: `expectedDrawing` (full `Database.Filename` + `FingerprintGuid`),
  `requireClean`, `backup`, optionally `preview` first, `saveDrawing`, then an independent readback
  (the `saved_drawing_readback` recipe reads the saved file).
- After a timeout, a fatal error or an unknown outcome, never resend the write automatically.
  Find out what happened first (journal, receipt, a read).
- Civil 3D may mark a drawing modified again right after a successful save when the write touched
  objects with dependents; this is not a failed write.
- Installing a plugin build needs every Civil 3D on that machine closed (`scripts/install.mjs`
  refuses otherwise). Do not close or restart Civil 3D sessions someone else is working in. Point a
  test client at the test instance with `CIVIL3D_PORT`.
- Record what was proven, on which commit, in `docs/validation/`, and carry the result into the
  recipe's `validation_summary` and `PROJECT_STATUS.md`, keeping proven, reported and unverified
  apart.

## Pull requests

Work on a branch and merge to `main` through a pull request; CI must be green. Keep PR text and
commits free of client, office and personal details, as for the rest of the repository.
