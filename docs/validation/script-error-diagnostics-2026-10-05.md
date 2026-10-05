# Civil 3D 2025 script error diagnostics validation

Date: 2026-10-05 (Europe/Budapest)

- Tested branch and implementation: `claude/script-error-diagnostics` at `3329baf038f15556998f809e925da82c5011f506`.
- Before-debug comparison: plugin built from `8eda40a8373697fe29f18ca8ec415d793558abae`. Its plugin source is unchanged in the tested commit's parent `a7c912be0f356026fa909f935e0d752cb0cf617e`.
- **Proven:** build, automated tests, live namespace hint and runtime line reporting succeeded. The exact `new Alignment().Lenght` example does **not** produce a `Length` hint in real Civil 3D because that type has no parameterless constructor. A valid typed expression does produce the intended hint.

## Build and automated checks

The candidate Release plugin built for `net8.0-windows` against five genuine local Autodesk reference DLLs. Each supplied reference independently matched its installed Civil 3D 2025 DLL by SHA-256: `accoremgd`, `AcDbMgd`, and `acmgd` version 25.0.0.0, `AecBaseMgd` 8.7.49.0, and `AeccDbMgd` 13.7.0.1458.

`npm ci --ignore-scripts` succeeded using a writable local cache, reporting zero npm advisories. `npm run test:plugin` passed with **86 .NET PASS lines and 1 Node test**, 0 failures. This includes all prior plugin checks and the 14 new Roslyn tests for cache behavior, member/type hints, runtime exception location, unknown names, and hint limits.

The plugin build had 0 errors and two `NU1900` warnings because the NuGet vulnerability feed could not be reached. The warning does not invalidate compilation or these functional tests. A complete NuGet vulnerability audit was not established by this build.

## Live Civil 3D results

Each DLL ran in its own fresh Civil 3D 2025 Hungary process with an empty stock-template drawing. The in-process module version ID and assembly location matched the DLL for that run. Calls used the actual `civil3d_query` MCP tool with a drawing identity guard and opt-in internal benchmark events. Exactly the established three public tools were exposed.

| Request | Result in the candidate process |
| --- | --- |
| `return new Alignment().Lenght;` | **No `Length` hint.** `CS1729`: `Alignment` has no constructor taking zero arguments. The compiler never emits the missing-member diagnostic for this expression. |
| `return ((Alignment)null).Lenght;` | `CS1061` and the correct hint: `Autodesk.Civil.DatabaseServices.Alignment has no member 'Lenght'. Similar members, including inherited: Length.` This probes the real Autodesk type without invoking a constructor. |
| `return typeof(SurfaceStyle).Name;` | `CS0246` and the correct `Autodesk.Civil.DatabaseServices.Styles` namespace, including a `using Autodesk.Civil.DatabaseServices.Styles;` suggestion. |
| Access to a null string's `Length` on line 3 | `CIVIL3D.TRANSACTION_FAILED`: `Script threw System.NullReferenceException at script line 3: Object reference not set to an instance of an object.` |
| A new valid script after the errors | Success with the expected value and `DBMOD=0`. |

The `new Alignment()` discrepancy comes from the real Civil 3D API. Claude can use a typed expression such as `((Alignment)null).Lenght` for this diagnostic check, or extend the feature if a hint is also required when construction fails first.

Three different, intentionally unknown names each produced `CS0103` with **no hint**, triggering the Autodesk/System namespace search. Complete internal `execution_ms` observations were **841.8, 785.4, and 733.0 ms** (median **785.4 ms**). Their Node-to-plugin end-to-end times were **928.6, 864.0, and 805.2 ms** (median **864.0 ms**). No request timed out.

Final identity readback found the same empty drawing at `DBMOD=0`; the plugin was idle with queue depth zero. Independent application readback confirmed the clean state before each process exited normally. No drawing save or write was requested. The originally installed plugin bundle was then restored and all eleven files matched the original hash inventory.

## Debug-information timing comparison

Ten distinct successful scripts were run in each fresh process. Each first run had one Roslyn cache miss and one compilation attempt; its immediate identical repeat had one hit and zero compilations. The source text, Node client, fixture type and sequence were the same for both versions.

| Internal benchmark measure | Before debug | New diagnostics DLL | Difference |
| --- | ---: | ---: | ---: |
| Median first-run `execution_ms` | 334.4 ms | 531.4 ms | +197.0 ms (+58.9%) |
| Median repeated-run `execution_ms` | 6.0 ms | 5.0 ms | -1.0 ms |
| Median paired first-minus-repeat time | 324.9 ms | 526.8 ms | **+201.8 ms (+62.1%)** |

**Proven:** these are the benchmark observations from this run. **Probable:** the debug information setting causes much of the extra first-run work; it is the material success-path change between the DLLs. **Unverified:** the exact isolated cost of `Script.Compile()` or a general speedup/slowdown across machines and drawings. The existing benchmark measures the whole command callback, not `Script.Compile()` separately. The paired first-minus-repeat value is a proxy that also includes metadata preparation, script emission and first execution. Each version had one process and ten paired samples, with the old version measured first.

Only this validation report is added to the branch; the implementation was not edited.
