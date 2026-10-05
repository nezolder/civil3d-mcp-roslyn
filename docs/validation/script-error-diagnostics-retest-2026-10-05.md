# Script diagnostics retest — Civil 3D 2025

Date: 2026-10-05 (Europe/Budapest)

- Tested branch: `claude/script-error-diagnostics`, source commit `65461507677fcb2c7d5d562d42f9fed7128c6ad0`.
- Comparison: the [previous live validation](script-error-diagnostics-2026-10-05.md) used the same ten-script sequence and the earlier `8eda40a8373697fe29f18ca8ec415d793558abae` plugin as its before-debug baseline.
- **Proven:** the updated success path is back in the prior timing range; the runtime exception still reports line 3. The first unknown-name error is slower than the next two. The second error remains measurably above the ordinary first-run median.

## Build and tests

A Release build for `net8.0-windows` succeeded with the genuine Civil 3D 2025 Autodesk references. All five supplied reference DLLs were SHA-256-identical to the installed DLLs. `npm run test:plugin` passed: **86 .NET PASS lines and 1 Node test, 0 failures**. The plugin build had 0 errors and two `NU1900` warnings because NuGet could not reach its vulnerability feed; this run does not establish a complete NuGet audit.

## Live timing

The newly built DLL was verified in-process by assembly location and module version ID in a fresh Civil 3D 2025 Hungary session. The drawing was an empty stock-template fixture. Every call used the actual `civil3d_query` MCP tool, a drawing identity guard, and the opt-in internal benchmark measurements.

As before, ten different successful scripts were each run once, then repeated immediately with identical text. Every first run was a cache miss with one compilation attempt; every repeat was a cache hit with zero compilations.

| Internal `execution_ms` median | Prior before-debug baseline | This retest |
| --- | ---: | ---: |
| First run of each distinct script | 334.4 ms | **281.5 ms** |
| Immediate identical repeat | 6.0 ms | **4.7 ms** |
| Paired first-minus-repeat difference | 324.9 ms | **271.5 ms** |

The prior debug-enabled DLL measured 531.4 ms for first runs. This retest's 281.5 ms is 52.9 ms below the prior before-debug median and 249.9 ms below that debug-enabled median. **Proven:** these are the observed medians from one process per version and ten paired scripts. The existing benchmark times the whole command callback, including compilation, emission and script execution; it does not isolate `Script.Compile()`. A general timing guarantee remains **Unverified**.

## Unknown names and runtime error

Three consecutive distinct unknown names each returned the expected `CIVIL3D.COMPILATION_ERROR` with `CS0103), no hint, one cache miss and one failed compilation. The type-name index is initially absent, so the first request includes its construction according to the tested source.

| Unknown-name call | Internal `execution_ms` | Node-to-plugin end-to-end |
| --- | ---: | ---: |
| First, including initial index build | **502.5 ms** | 507.8 ms |
| Second | **425.4 ms** | 592.7 ms |
| Third | **321.6 ms** | 372.2 ms |

For scale, the successful first-run median above is 281.5 ms. The second unknown-name error is 143.9 ms above that value; the third is 40.1 ms above it. The measured times decline, but these whole-request observations cannot isolate index construction or prove why the second remains slower. No call timed out.

The multiline null-object probe returned the exact expected message on the updated runtime path:

```text
Script threw System.NullReferenceException at script line 3: Object reference not set to an instance of an object.
```

Final independent identity readback found the same empty fixture at `DBMOD=0`, and private health showed an idle plugin with queue depth zero. The test process exited normally. The original installed plugin bundle was restored; all eleven files matched its pre-test hash inventory. No drawing write or save was issued.

Only this retest report is added to the branch; the tested implementation was not edited.
