# Published development status

Updated: 2026-11-11

This repository contains the accepted runtime, six focused engineering recipes with scoped evidence, and an explicitly experimental, offline-validated surface-profile recipe. Client data, live-run artifacts, proprietary assemblies and private development history are excluded.

## Current catalogue

The catalogue contains **39 skills** with **42 C# templates**; **26** of the skills run by name. These numbers, and the ones in the README, are compared with the `skills/` folder by `tests/skills/documented-counts.test.mjs`, so a change to the folder fails the test until the text follows. Counts inside the dated sections below describe the state at their date and are not updated.

## Operational safety from the architecture review (2026-11-11)

The second group of review findings concerns running the server day to day: which version runs, what a write did, and how several Civil 3D instances are told apart. The three public tools remain; `civil3d_execute` gains three optional inputs and `civil3d_skills` one action.

- **Choosing an instance without commands.** With several Civil 3D instances the host used to run an internal command in every one of them before each guarded call, also in instances another assistant was working in. The plugin now remembers its active drawing and repeats it in the health reply; the host matches `expectedDrawing` against those replies. The drawing guard in the plugin is unchanged and still decides.
- **Safety switches.** `requireClean` refuses a drawing with unsaved changes before the script runs; `backup` first copies the saved DWG to a time-stamped file (`backupDirectory` chooses the folder). An older plugin that would ignore them is detected and the call refused.
- **Receipt.** Every write that reached the drawing returns, next to its result, the drawing, `DBMOD` before and after, whether it was committed and saved, the file's size and time, and the backup path.
- **Journal.** The plugin appends one line per write request to a local monthly file: time, session, drawing, description, code hash, outcome. No code and no result. The `description` of a write is also shown on the Civil 3D command line.
- **Versions.** Server and plugin record the commit and time of their build. `civil3d_skills` `action: "status"` shows both, the running instances and their active drawings, and warns when the two sides come from different commits.
- **Installing.** `scripts/install.mjs` copies the built server to a folder of its own and the built plugin into the autoload bundle, with a backup of what it replaces. It does not change any client configuration.
- **Stale endpoint records.** A record whose process ID was reused by another program is now recognised by the program name; previously it was kept when the new owner was a system process.
- **Tests stay on their own.** Discovery also tries the default port 8080. With `CIVIL3D_MCP_DEFAULT_PORT_FALLBACK=0` it uses the endpoint records only; the routing tests set it, so they cannot reach a Civil 3D that runs on the same machine.
- **Documented counts.** The skill counts in the README and in this file are tied to the `skills/` folder by a test. The count in the discovery test is computed from the folder.

Evidence boundaries:

- **Proven offline:** plugin core tests for the remembered drawing (unknown, known, switched, none open, refreshed after an operation), `requireClean`, the backup (made before the script, never overwriting, refused when unsaved, unnamed or uncopyable), the receipt on success, failure and refusal, the journal content and its failure isolation, the command-line text, and the program-name check; Node tests for routing two instances without any identity command, the fallback for an older plugin, the feature check before a safety switch is sent, the receipt block, the status action and the install script. The plugin builds against the Civil 3D 2025 assemblies.
- **Proven live (2026-11-11, commit `3eeec27`, two Civil 3D 2025 instances on disposable copies):** the status action with matching builds; routing by `expectedDrawing` with no identity command in either instance; the refusal without a target; `requireClean` accepting a saved and refusing an unsaved drawing; `DBMOD` 0 after a host-managed save, so the switch works on consecutive saved writes; the backup of a drawing that Civil 3D holds open, in the default and in a given folder; the receipt on success, refusal and a failed script; the journal written by both instances; the description on the command line; the remembered drawing following a drawing switch within about half a second; both halves of the install script, including its refusal while Civil 3D runs; removal of a stale record whose process ID belongs to another program. Report: `docs/validation/operational-safety-3eeec27-2026-11-11.md`.
- **Observed, not explained:** one of the two freshly opened copies showed `DBMOD` 1 before any call. `requireClean` refuses such a drawing until it is saved.
- **Unverified in Civil 3D:** three or more instances; an older plugin next to a new one; a backup from a network drive or of a very large drawing; two instances writing the journal in the same moment.

## Reliability fixes from the architecture review (2026-11-10)

A read-only review of the whole server found several defects in timeouts, cancellation, discovery and result handling. This update corrects the first group. The three public tools and their existing inputs are unchanged; `timeoutSeconds` is a new optional input on the two code tools.

- **Script time limit.** The limit was fixed at 120 seconds on both sides. The Node host started its timer first and gave up first, so a slow call was reported as `outcome: "unknown"`; a script without loops could then still finish and commit with nobody listening. Now a call can set `timeoutSeconds` (1–1800), the host always waits longer than the plugin's limit, and a script that returns after its limit, or after the client is gone, is not committed. `CIVIL3D.TIMEOUT` is therefore a definite `reported_error`.
- **Client cancel.** A cancelled tool call (Stop) now closes the plugin connection, which stops the script at its next loop iteration and rolls it back. Previously the script ran on until the host timeout.
- **Stale endpoint records.** Records left by a crashed or killed Civil 3D were probed on every call. The host now skips records whose process is gone, and each new plugin session deletes them.
- **Skill search.** A query with one unknown word returned nothing. The closest skills are now returned and flagged `partial`.
- **Read-only drawings.** `saveDrawing` on a drawing opened read-only is refused before the script runs (`CIVIL3D.DRAWING_READ_ONLY`), instead of committing in memory and failing at the save.
- **NaN and Infinity.** One non-finite number failed the whole result without saying where. It now becomes `null`, with its path listed under `nonFinite`.
- **Ambiguous type names.** Bare `DBObject`, `Entity`, `ErrorStatus`, `Exception`, `Surface` and `Section` exist in two auto-imported namespaces and failed with CS0104. Each now has a default meaning; full names and the script's own aliases still win.
- **Tool annotations and dependencies.** The tools carry MCP read-only/destructive hints. `@modelcontextprotocol/sdk` is 1.32.2 and `zod` 4.6.7.

Evidence boundaries:

- **Proven offline:** Node tests for the reply wait, `timeoutSeconds` validation and forwarding, cancellation before and after sending, stale-record skipping with fallback, partial search and annotations. Host-independent .NET tests run the real Roslyn executor for the per-call limit, the discarded late result, the default aliases and their overrides, and non-finite serialization; plugin core tests cover the read-only refusal, limit validation and stale-record cleanup. The six aliased names were confirmed ambiguous against Civil 3D 2025 metadata, the plugin builds against those assemblies, and all 42 recipe code blocks compile both with and without the aliases.
- **Proven in a targeted Civil 3D 2025 check** ([report](docs/validation/reliability-fixes-9ac927e-2026-11-10.md)), one fresh instance on a disposable drawing under hu-HU: the default aliases, non-finite results, a 5-second and the default 120-second limit on looping scripts, a loop-free write discarded when it returned after its limit, a 130-second write under a 180-second limit, a client cancel that stopped a looping write without committing it, the read-only refusal before the script, a normal host-managed save, and removal of a stale endpoint record at plugin start.
- **Unverified:** a raised limit on a real long Civil operation, Stop pressed in a desktop client (the test sent the same MCP cancel from an SDK client), and stale-record handling while several Civil 3D instances run.

## Runtime and named-recipe updates (2026-10-05–06)

The accepted runtime now includes a bounded compiled-script cache keyed by exact source text, compilation/member/namespace diagnostics and runtime source-line reporting, cooperative loop cancellation, explicit rejection of asynchronous `await`, and broader bounded result serialization. Reviewed recipes can also run by name with validated parameters through the existing query/execute tools. The public surface remains exactly three tools; dynamic Roslyn execution, drawing guards, host-managed saving and the no-automatic-retry contract remain.

- **Proven in scoped offline and Civil 3D 2025 checks:** exact-code cache hits, compilation-error repeats followed by successful queries, and bounded eviction/collision tests. The [cache report](docs/validation/script-cache-2026-10-05.md) records the tested implementation and comparison baseline. General speedups and long-session memory behavior remain **Unverified**.
- **Proven in scoped checks:** a valid typed missing-member probe, namespace hints and runtime line reporting. The [initial diagnostics report](docs/validation/script-error-diagnostics-2026-10-05.md) and [retest](docs/validation/script-error-diagnostics-retest-2026-10-05.md) preserve the constructor limitation and residual unknown-name latency. A source-equivalent hint is not proof that every malformed expression can be corrected.
- **Proven in disposable live fixtures:** loop timeout/disconnect cancellation, write rollback and abandonment of a queued request. The [cancellation report](docs/validation/script-cancellation-2026-10-05.md) records the independent readbacks. Long individual Civil API calls are not interruptible, and these cases do not prove recovery of arbitrary native operations.
- **Proven in scoped live checks:** `await` is rejected with `MCP0001`, and subsequent ordinary and named queries succeed. See the [await-rejection report](docs/validation/await-rejection-91c55ad-2026-10-05.md).
- **Proven in scoped offline and live checks:** named execution of reviewed recipes, bound-parameter validation and the corrected Windows CRLF regression. The [named-recipe retest](docs/validation/run-by-name-9324388-2026-10-05.md) and [saved named-write/readback report](docs/validation/run-by-name-live-write-2026-10-05.md) establish one polyline-to-alignment write, save and independent reopen, plus specific populated reads. Broader production-model coverage and other named writes remain **Unverified**.
- **Proven in scoped checks at `b75e765`:** LINQ/lazy sequences, dates, tuples, script-defined return types and explicit collection-truncation metadata. The [serialization report](docs/validation/result-serialization-b75e765-2026-10-06.md) records 104 .NET cases and the targeted live query results. The live run did not repeat the offline runaway-lazy-loop cases; arbitrary custom getters and general performance remain **Unverified**.

The later accepted dependency-only update installs `proxy-addr 2.0.8`; the retained local receipt records a successful Node build, 101 Node tests, a fresh three-tool MCP smoke check and zero known npm advisories at that check. The Civil plugin source was unchanged by that dependency update. These are recorded results, not a fresh live certification of every capability. Existing desktop clients still need reconnect/restart after Node module or tool-description updates.

The older sections below retain their original dates and evidence scopes. The linked reports provide the newer runtime details; historical test totals must not be treated as the current suite size.

## Recipe discovery metadata (2026-10-05)

The 29 stable recipes now carry specific Hungarian/English task aliases, workflow tags, a tested Civil version and a scoped validation summary. List/search expose write scope and concise evidence; aliases are included in the full `get` response. Search normalizes accents and hyphens while preserving all-word matching, category filtering and pagination. See [the catalogue contract](docs/SKILL_CATALOG.md).

The extension is additive. Missing or invalid evidence remains unknown, and exact-key consumers must accept the new fields. Existing names, categories, descriptions, parameter definitions, write requirements and all C# recipe bodies are unchanged. The Node catalogue module changes; the Civil plugin, transport, save and retry contracts are unchanged. A newly started MCP server loads the new behavior; existing servers need a client reconnect/restart.

**Proven offline:** TypeScript typecheck/build, all 24 skill/API Node tests, metadata compilation of 32 Civil 3D 2025 templates, 83 host-free input/math cases and the three-tool/29-skill MCP catalogue check passed. Fixtures cover legacy defaults, quoted inline metadata, CRLF/Unicode, malformed fields and real Hungarian/English task queries. The control-character regression fails before the fix and passes after it. The catalogue checks do not connect to or modify a Civil drawing.

**Unverified:** automatic task selection quality, general latency/token savings and new live engineering cases. The recorded version includes offline API compilation and does not imply live certification. Existing recipe-specific evidence and remaining limitations are preserved.

## Boundaries and evidence

- Autodesk Civil 3D 2025 is the primary target; retained live checks used Civil 3D 2025 Hungary. Other versions are **Unverified**.
- Dynamic Roslyn/C# execution remains the core, with exactly `civil3d_query`, `civil3d_execute` and `civil3d_skills` as public MCP tools.
- **Proven** means supported by the stated code, offline test or targeted live evidence. **Probable** means a supported explanation that is not fully established. **Unverified** means a fresh targeted check is still needed.
- Offline tests do not establish live Civil behavior. The live results below concern specific disposable fixtures, not universal feature or road-standard compliance.

## Included capabilities

| Capability | Evidence and limits |
| --- | --- |
| Benchmark recorder and opt-in read-only runner | **Proven offline and for one live query scenario:** five cold and five warm first-pass successes. Other benchmark scenarios and total workflow/token savings remain **Unverified**. |
| Structured errors, bounded framing/results, serialized execution and drawing guards | **Proven offline**, with targeted normal live execution and saved-write checks. Uncertain outcomes are never retried automatically. |
| Private health, audit logging and session idempotency | **Proven offline**, with responsive live health during pending work. Logs omit code and drawing content. Idempotency is session-scoped, not durable exactly-once execution. |
| Filtered/paged skills and bounded API lookup | **Proven offline**; skill discovery was also checked through a fresh client. Lookup reads already-loaded allowlisted public metadata without running Civil code. |
| Multiple-instance routing | **Proven live:** two Civil sessions used distinct endpoints, ambiguous access failed closed, and selected requests reached the intended drawing fingerprints with `DBMOD=0`. |
| Bounded road-model inventories | **Proven offline and on an empty live fixture:** profiles, corridors, sample lines/sections, gravity pipe-network QC, TIN definition counts, selection and drawing identity. Populated cases are not all live-validated. |
| Road-design style readiness | **Proven live** for twelve baseline style groups in one Hungary template. Other templates remain **Unverified**. |
| Alignment from an identified polyline | **Proven live**, including saved-file reopen. The source is preserved; this narrow recipe adds no automatic curves or standards decisions. |
| Connected alignment geometry audit | **Proven offline and live** on straight and compound line/arc/clothoid geometry. Standards compliance is explicitly not evaluated. |
| Fixed-primitive alignment replacement | **Proven offline and in one isolated live case**, including save/reopen of an eight-primitive reverse-clothoid chain. The recipe requires an audited independent, siteless, zero-station centreline without station equations, superelevation, criteria/check state, profiles or corridor dependencies. |
| Six reusable engineering recipes | Explicit-PVI design profiles, individual section views, native material quantities, sampled TIN differences, data-reference state and corridor targets. **Proven offline**, with the scoped live evidence and remaining limits below. See the [recipe guide](docs/ENGINEERING_SKILLS.md). |
| Experimental surface profile and ordinary profile view | **Proven offline; live authoring Unverified.** Creates one full-length dynamic profile from an explicitly identified local alignment and TIN surface, plus one view using existing named styles. Rejects reference/volume sources, duplicate names and invalid inputs. Finite endpoint elevations do not prove that the surface has no interior gaps. |
| Post-commit saving | **Proven live** with `saveDrawing: true`, `DBMOD=0` and independent readback. Saving occurs after transaction/lock disposal; scripts must not call `Database.SaveAs` or queue `QSAVE`. |

At the 2026-10-02 publication the catalog contained 29 skills and 32 C# templates, behind the same three public tools; the current numbers are under [Current catalogue](#current-catalogue). The surface-profile recipe is experimental and has no live authoring evidence. Build instructions and test commands remain in [README.md](README.md) and `package.json`; Autodesk reference assemblies must be supplied locally and are not distributed.

**Proven offline for the previous accepted snapshot (2026-09-03):** TypeScript typecheck/build, plugin Release build, benchmark/error/discovery/routing tests, plugin core and serialization tests, transport tests, the three-tool MCP smoke check, all 24 template metadata compilations and 18 fixed-primitive input checks passed. NuGet vulnerability-feed retrieval produced an environment warning; these checks are not a dependency-security audit.

## Node search, output and configuration correction (2026-10-02)

The Node server now matches all normalized search words across skill and parameter metadata, returns compact success JSON, defaults to the plugin's IPv4 loopback listener and falls back safely for invalid command/save timeouts or log levels. Public tool descriptions clarify synchronous Civil scripts and the bounded return-value contract. The unused direct WebSocket dependency was removed, and six transitive packages were updated within the dependency lockfile.

The submitted patch was reviewed against the current public source. The review corrected inherited-property names being accepted as log levels, normalized-empty searches returning the whole catalog, and incomplete namespace/result guidance. Timeout, logging, parameter-search, empty-search and compact query/execute/save regressions are covered by focused tests.

**Proven offline on the publication files:** TypeScript build, all 88 Node tests (benchmark, errors, routing, transport, drawing guards and skill discovery), and the three-tool/29-skill MCP smoke check passed. A clean lockfile installation with lifecycle scripts disabled succeeded; npm's dependency audit reported zero known vulnerabilities at the time of the check. This audit is limited to the package advisory data.

**Unverified:** general runtime latency/token savings and fresh live Civil 3D behavior after this Node update. The integration tests use an isolated local TCP mock; they do not load or modify a Civil drawing. The Civil plugin and its C# execution/save/retry contracts are unchanged. Existing recipe evidence below retains its original scope.

## Reusable engineering recipes and publication checks (2026-10-02)

Six focused recipes use the existing skill catalog and dynamic C# execution path. The plugin runtime and Node implementation are unchanged. Profiles require explicit approved PVI values; section views require an existing sampled group and named styles. The four read recipes report existing native quantities, sampled elevations, reference state or target assignments without repairing the model.

**Proven offline on the publication files:** TypeScript build, all 15 skill/API-discovery tests, metadata compilation of all 32 C# templates, 18 fixed-primitive, 11 surface-profile and 54 engineering input/math cases, and the three-tool/29-skill MCP smoke check passed. These checks did not open or modify a Civil drawing.

**Proven in retained, scoped Civil 3D 2025 evidence (2026-10-01):** a three-PVI design profile with one symmetric parabola survived authoring/save/independent reopen. Three section views passed complete independent saved-state readback, empty-band checks, requested bounds and grid-overlap checks. A known +1 TIN comparison and an out-of-surface point exercised all-point statistics and coverage; an existing three-section native material list returned the independently expected fill total. All four targeted read recipes retained `DBMOD=0`. Reference reporting was checked on local objects, and the corridor audit on an empty corridor with one baseline.

**Unverified:** data-bearing DREF source states, populated corridor target warnings, broader profile combinations, populated section-view bands, section-view saved-file reopen/print quality, model freshness and road-standard compliance. Saved-state readback is not a section-view reopen. Post-creation native dirty flags were reconciled with a geometry-neutral host-managed save and independent `DBMOD=0` checks; successful creation was not repeated and the runtime save/retry contract did not change.

Detail caps do not establish complete coverage. Surface statistics include all supplied valid points; reference and target scans report incomplete coverage explicitly. Native material rows represent intervals ending at their stations, so selected rows do not prove an exactly clipped volume at arbitrary range boundaries. Inputs, limits and upstream idea/license attribution are recorded in [docs/ENGINEERING_SKILLS.md](docs/ENGINEERING_SKILLS.md).

## Experimental surface-profile recipe (2026-09-28)

The recipe and its tests are now published with their evidence limits. The plugin runtime and Node implementation are unchanged. Use the existing drawing guard, outer transaction and optional `saveDrawing` path; the recipe does not save or commit independently. A fresh targeted Civil 3D 2025 authoring/save/readback test is still required before claiming live validation.

**Proven offline on the publication files:** TypeScript build, all 14 skill/API-discovery tests, metadata compilation of all 25 C# templates, 18 fixed-primitive and 11 surface-profile input cases, and the three-tool/23-skill MCP smoke check passed. These checks did not open or modify a Civil drawing. Dependency vulnerability auditing was disabled for the metadata test restore; this is not a dependency-security audit.

## Offline C# code-body checks (2026-09-28)

The skill test runner now accepts `--code` followed by an absolute `.cs` file path. It uses the local Civil 3D metadata and standard context parameters to report compilation diagnostics without executing the body. Invalid arguments, missing input files and missing Autodesk references return exit code 2; compilation errors return 1. The no-argument catalog checks remain available.

**Proven offline on the publication files:** all 25 template compilations and 29 input cases still pass. Eight focused command-line checks passed: a valid Civil API body, an invalid API member, a throwing body that was not executed, relative paths, missing files, wrong extensions, unknown flags and missing reference assemblies. **Unverified:** successful metadata compilation does not prove live Civil behavior or runtime loading.

## Modal command completion

- **Selection compatibility follow-up:** the internal modal command also carries `UsePickSet | Redraw`, preserving implied/PickFirst selection when entering the command and reading it. It remains modal and does not suppress Undo markers; no `NoUndoMarker` flag was added.
- **Proven offline for the follow-up:** all 66 core/serialization cases pass, including 20 shared contracts exercised under both native and modal dispatch. The test assembly now compiles the production plugin entry and checks its command metadata. Native-awaitable diagnostics remain backend-specific. Test host doubles do not establish live selection, Undo, or latency behavior.
- **Proven in a targeted Civil 3D 2025 selection check:** two synthetic lines remained selected after an unrelated query, two consecutive selection queries, and a geometry-neutral execute followed by another selection query, with `DBMOD=0`. This used an isolated source-equivalent test module with distinct command names to coexist with the installed runtime; the module was removed afterwards and diagnostics stayed disabled. Broader Undo/Redo behavior and general small-query latency remain **Unverified**.

- **Proven in code and targeted Civil 3D 2025 checks:** the default backend runs the existing dynamic Roslyn body through a genuine modal command, avoiding the native `ExecuteInCommandContextAsync` completion path. An isolated comparison using identical drawing copies and the same rebuild/save operation reproduced the old completion hang; both the candidate and the final installed modal build returned, and subsequent queries also completed.
- The same drawing guard, transaction, post-commit save and serialization gate remain. The gate requires both body disposal and the matching command lifecycle event. Only an opaque one-use token enters the command input; abandoned tokens cannot run later requests. Started work is never released on timeout or automatically replayed.
- Private health reports `executionBackend`. `CIVIL3D_MCP_EXECUTION_BACKEND=native` is reserved for an explicitly selected fresh-session comparison; there is no automatic fallback. Detailed completion diagnostics are disabled by default and contain only bounded, fixed metadata when explicitly enabled.
- **Proven offline for this update:** the plugin build, plugin core/serialization and modal admission/lifecycle tests, TypeScript checks and the three-tool MCP smoke check passed. This is targeted execution-completion evidence, not proof of every possible long-running or asynchronous script scenario.
- Corridor dependency freshness is outside this execution-completion fix. This update does not infer an MCP defect from a corridor's stale flag, and does not change corridor or data-reference modeling behavior.

## Earlier command-context correction

- **Proven offline:** a 15-second atomic admission deadline rejects a callback that has not started, returning `CIVIL3D.COMMAND_CONTEXT_TIMEOUT`, `outcome: not_started` and `retryable: false`. A late abandoned callback does no drawing work. Once an operation starts, its gate is retained until native completion.
- **Proven local API inspection and deterministic reproduction:** a completion recheck closes a lost-notification window in the Civil 3D 2025 native awaitable. The completion race is a **Probable** contributor to earlier hangs, not a proven explanation of every case.
- **Proven offline:** private health reports only fixed stage names and elapsed times. Failure/cancellation, stale callbacks, subsequent requests and started-operation serialization are covered by regression tests.
- **Proven live on the source-equivalent development build:** with no drawing open, one request returned the non-retryable pre-start error after about 15 seconds and health returned to idle. After opening a disposable drawing, a new request succeeded in the same Civil process without restart. A separate backed-up disposable copy completed a geometry-neutral save, follow-up query and normal close/reopen with `DBMOD=0`.
- **Unverified:** elimination of every intermittent post-save or side-database hang, native cancellation, or recovery of arbitrary already-running code. Do not clear the gate or repeat an uncertain write as automatic recovery.

The publication build is checked offline; it is not a separate live installation. Public source is aligned with the accepted development implementation, and this source update does not alter the active Civil session.

## Not yet included

Automatic grade design, general corridor authoring, automatic standards compliance and later Civil version support are not claimed. The explicit-PVI recipe creates a profile from supplied values; it does not choose design criteria. The experimental surface-profile/profile-view recipe still has no live authoring validation.

Further development should address an observed work problem or explicitly approved validation. Do not introduce a general refactor or new public tool without a concrete demonstrated need.
