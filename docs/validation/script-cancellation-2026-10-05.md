# Script cancellation — Civil 3D 2025 live validation

Date: 2026-10-05 (Europe/Budapest). Tested `claude/kind-volta-rsvbm5` at `cc5137d646c699964e1796ed91e599e395100d84`. Timing comparison used the then-installed `main` at `b925c6cee85b8575c20f7857b56b754879bd74c7`. The live session used Civil 3D 2025 with the Hungary profile and a disposable empty stock-template drawing. No client drawing was opened or saved.

## Build and tests

- **Proven:** Release `net8.0-windows` plugin build succeeded with five references verified byte-for-byte against the installed Autodesk Civil 3D 2025 assemblies (0 warnings, 0 errors).
- **Proven:** `npm run test:plugin` passed: 93 .NET cases and 1 Node test. `npm run test:transport` passed: 7 .NET cases and 8 Node tests. There were no failures.

## Live cancellation results

| Probe | Result | Subsequent state |
| --- | --- | --- |
| `civil3d_query` with `while (true) { }`, Node timeout set above the plugin's 120 s limit | `CIVIL3D.TIMEOUT` after 120.3 s | A follow-up query succeeded; idle queue depth 0 and `DBMOD=0`. |
| Same query with `CIVIL3D_COMMAND_TIMEOUT=10000` | `CIVIL3D.COMMAND_TIMEOUT` after 10.0 s | The client-disconnect path stopped the loop; a follow-up query succeeded; idle queue depth 0 and `DBMOD=0`. |
| `civil3d_execute` creates a unique text object, then loops indefinitely; 10 s Node timeout | `CIVIL3D.COMMAND_TIMEOUT` after 10.0 s | The object was absent in the first guarded readback and a separate independent readback. The write transaction rolled back. |
| One active looping query with a 15 s Node timeout; a second, marker-creating execute queued with a 3 s Node timeout | Health showed `RunningScript` and queue depth 1. The queued request timed out at 3.0 s; the first at 15.0 s. | The queued object was absent in immediate and later readbacks; final queue depth 0. |

**Proven additional observation:** after the cancelled write, the object was absent but AutoCAD reported `DBMOD=1`. The initial harness therefore exited nonzero on its extra `DBMOD=0` assertion, although the requested object-rollback condition passed. The unsaved disposable drawing was discarded without saving. Whether this dirty flag is expected AutoCAD behavior for an aborted object creation or calls for a plugin correction remains **Unverified**. No uncertain write was repeated.

## Normal-script timing

Ten distinct successful loop-free scripts were each run once, then repeated with identical text. All first runs were cache misses with one compilation; all repeats were cache hits without compilation. Internal `execution_ms` covers the command callback, not compilation alone.

| Median of ten | `main` comparison | candidate | Difference |
| --- | ---: | ---: | ---: |
| First execution | 240.45 ms | 246.89 ms | +6.44 ms (+2.68%) |
| Identical repeat | 0.506 ms | 0.508 ms | +0.002 ms |
| First end-to-end call | 260.57 ms | 254.39 ms | -6.18 ms |
| Repeat end-to-end call | 9.62 ms | 10.06 ms | +0.44 ms |

**Proven:** these are the observed medians from one local session per version; no large slowdown appeared for these normal scripts. Broader host-load and script-mix performance remains **Unverified**.

After testing, the candidate process closed, the complete previous plugin bundle was hash-verified on restoration, and a fresh Civil 3D Hungary session loaded the comparison `main` plugin with `DBMOD=0`. This validation report contains only aggregate timings and synthetic test outcomes; local logs, drawing identities, paths, and raw benchmark traces remain private.
