# Development status

Updated: 2026-08-25

This file is the compact handoff for the current development line. It separates repository and test evidence from live Civil 3D evidence.

## Fixed project boundaries

- Dynamic Roslyn/C# execution remains the core design.
- The public MCP surface remains exactly `civil3d_query`, `civil3d_execute`, and `civil3d_skills`.
- Civil 3D 2025 is the primary live target. Later-version support must be added separately and must not break 2025.
- A build, typecheck, or MCP smoke test is not live Civil 3D proof.
- Changes should remain small, reversible, and independently testable.

Status terms:

- **Proven**: supported by the current repository, tests, or retained local live evidence.
- **Probable**: strongly supported but not fully rechecked in the current environment.
- **Unverified**: requires a fresh targeted check.

## Phase status

| Phase | Status | Evidence boundary |
| --- | --- | --- |
| 0–1 audit, preserved baseline, migration | **Proven** | Preserved commit `5ce5049` and tag `local-baseline-c3d2025-20260820`; the current line descends from it. |
| 2A benchmark harness | **Proven** | Commits `b61dd63`, `efb737d`, and `b905d29`. The read-only query scenario has 5 cold and 5 warm live runs, all successful on the first pass. This does not cover every scenario in the benchmark manifest. |
| 2B structured errors | **Proven in code/tests** | Commit `e259267`. Fresh live checks of each error class are **Unverified**. |
| 2C framed and bounded transport | **Proven in code/tests** | Commit `dd04b31`. Later live calls prove the normal transport path; destructive limit-edge testing remains unnecessary unless a fault appears. |
| 2D serialized execution and drawing guard | **Proven in code/tests** | Commit `ee6d9da`. Later live probes completed without a stuck operation. Broad concurrency stress testing is **Unverified** and is not currently required. |
| 2E bounded Civil 3D result serialization | **Proven in code/tests** | Commit `0da7516`. |
| 2F health/status | **Proven live** | Commit `0f57133`; live status returned idle state after the latest save test. |
| 2G filtered and paged skill discovery | **Proven in code/tests** | Commit `691f174`. This is Node-side behavior and does not require a drawing write. |
| 2H bounded read-only API lookup | **Proven in code/tests** | Commit `82f5aeb`. A fresh live recheck is **Unverified** in this handoff. |
| 2I audit, security, and session idempotency | **Proven in code/tests** | Commits `a7a1552`, `56fd701`, and `227e88f`. |
| 2J validated skills and stable helpers | **Partly proven** | Civil 3D 2025 skill fixes and tests are in `643e422`, `6f66965`, and `15f03a7`. The retained live benchmark covers one read-only query, not the complete skills surface. |

## Latest corrective fix

Commit `19c8574` adds the optional `saveDrawing` behavior to the existing execute tool. Saving happens only after the script transaction and document lock are closed. A live Civil 3D 2025 Hungary test on a disposable drawing proved an in-memory change, successful disk save, `DBMOD=0`, and independent disk readback. An unnamed drawing was rejected before execution so its template path could not be overwritten.

## Next decision gate

Close 2J with a deliberately small acceptance check, without new implementation unless it exposes a real fault:

1. Start a fresh Codex/ChatGPT client session so the MCP tool schema is not cached.
2. Confirm exactly three public tools and confirm that `civil3d_execute` exposes `saveDrawing`.
3. Check skill list/get/filter/pagination through the public MCP surface.
4. If needed, run at most two representative read-only skills in Civil 3D 2025 Hungary on a disposable synthetic drawing and retain only sanitized evidence.

The empty write and skills groups in the read-only benchmark aggregate are not a reason to build a write-capable benchmark runner. They reflect the runner's intentional read-only scope.

After this gate, choose the next feature only from an observed work problem or a measured benefit. Do not begin a general refactor.
