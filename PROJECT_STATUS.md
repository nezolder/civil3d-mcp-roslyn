# Published development status

Updated: 2026-09-11

This repository contains the complete current accepted source snapshot, not just a corrective patch on the initial release. Unvalidated local experiments, client data, live-run artifacts, proprietary assemblies and private development history are excluded.

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
| Post-commit saving | **Proven live** with `saveDrawing: true`, `DBMOD=0` and independent readback. Saving occurs after transaction/lock disposal; scripts must not call `Database.SaveAs` or queue `QSAVE`. |

The accepted catalog contains **22 skills** and **24 C# templates**, behind the same three public tools. Build instructions and test commands remain in [README.md](README.md) and `package.json`; Autodesk reference assemblies must be supplied locally and are not distributed.

**Proven offline for the previous accepted snapshot (2026-09-03):** TypeScript typecheck/build, plugin Release build, benchmark/error/discovery/routing tests, plugin core and serialization tests, transport tests, the three-tool MCP smoke check, all 24 template metadata compilations and 18 fixed-primitive input checks passed. The catalog and Node implementation are unchanged in this update. NuGet vulnerability-feed retrieval produced an environment warning; these checks are not a dependency-security audit.

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

Dynamic surface-profile/profile-view authoring remains local development work. Its offline checks passed, but its recipe has not been run live, so it is excluded from this accepted snapshot. Designed grades, general corridor authoring, automatic standards compliance and later Civil version support are not claimed.

Further development should address an observed work problem or explicitly approved validation. Do not introduce a general refactor or new public tool without a concrete demonstrated need.
