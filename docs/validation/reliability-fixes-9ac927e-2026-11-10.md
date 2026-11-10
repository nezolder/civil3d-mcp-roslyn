# Reliability fixes from the architecture review: live validation

Date: 2026-11-10 (Europe/Budapest). Tested commit: `9ac927e` (script time limit, client cancel, stale endpoint records, partial skill search, read-only refusal, non-finite numbers, default type aliases).

## Fixture and method

**Proven:** the live host was Civil 3D 2025 (Hungarian Metric profile) on the user's second PC, regional setting hu-HU. No other Civil 3D was running. The source was cloned at `9ac927e` into a temporary folder and built there (TypeScript build and `dotnet build -c Release`, 0 warnings, 0 errors, against the reference assemblies of the local installation). The installed plugin bundle was backed up, the built plugin was copied into it, one fresh Civil 3D instance was started on a disposable drawing (a copy of a stock metric template), and after the run the bundle was restored and verified file by file by hash. The temporary folder was deleted.

The Node server of the same build was driven over stdio by a scratch MCP client with `CIVIL3D_PORT` fixed to the test instance. No work drawing was opened.

Two manual clicks were needed: AutoCAD's unsigned-executable prompt for the newly built DLL ("load once" was chosen), and the save question when the test instance was closed (not saved).

## Host-free tests on the same machine

Under hu-HU: plugin core tests 78 of 78, Roslyn executor tests 37 of 37, the Node plugin test 1 of 1. An earlier run of commit `4be1197` on another hu-HU machine failed one Roslyn test because the timeout text was formatted with a decimal comma; `9ac927e` formats it invariantly, and the CI workflow now runs under hu-HU.

## Live results

| Case | Expected | Observed |
|---|---|---|
| Default aliases | Bare `Surface`, `Section` are the Civil types; `Entity`, `DBObject`, `ErrorStatus` the AutoCAD types; `Exception` is `System.Exception` | As expected (733 ms). Bare `Table` still failed with `CIVIL3D.COMPILATION_ERROR` / CS0104. A script with its own `using Surface = Autodesk.AutoCAD.DatabaseServices.Surface;` compiled and resolved to the AutoCAD type. |
| Non-finite numbers | `null` plus `nonFinite` | `{a: null, b: 1.5, c: null}`, `nonFinite {count: 2, paths: ["$.a", "$.c"]}` |
| `timeoutSeconds` 5, endless loop (query) | `CIVIL3D.TIMEOUT`, `reported_error`, about 5 s | 5.16 s, message "after 5s"; the next query answered in 205 ms |
| `timeoutSeconds` 5, loop-free write blocking 8 s | `CIVIL3D.TIMEOUT`, nothing committed | `CIVIL3D.TIMEOUT` (`reported_error`) after 8.24 s, when the blocking call returned; the layer created by the script did not exist afterwards |
| `timeoutSeconds` 180, write blocking 130 s | Success | Success after 130.95 s; the layer exists |
| Default limit, endless loop (query) | `CIVIL3D.TIMEOUT` from the plugin at about 120 s, not an unknown outcome from the host | 120.03 s, source `plugin`, `reported_error`, "after 120s"; the next query answered in 38 ms |
| Client cancel 3 s into a write that then loops forever | Call ends promptly, script stops, nothing committed | Call ended at 3.00 s; host audit `CIVIL3D.CANCELLED`, source `node`, outcome `unknown`; the next query answered in 45 ms; the layer did not exist |
| `saveDrawing: true` on the disposable drawing | Success, file written | Success in 1.72 s; file time and size changed, layer present, `DBMOD` 0 |
| Drawing opened read-only | `drawing_info` reports it; execute with `saveDrawing: true` refused before the script | `isReadOnly: true`; `CIVIL3D.DRAWING_READ_ONLY`, outcome `not_started`, 27 ms; layers, `DBMOD`, file hash and file time unchanged |
| Skill search with one unknown word | Closest skills, `partial: true` | Two skills, `partial: true`, 2 of 3 words matched |
| Stale endpoint records | A starting plugin removes records of dead processes | The one stale record present was removed when the test instance started; at exit the instance removed its own record |

Notes:

- The read-only drawing was opened from a script with `DocumentManager.Open(path, true)` and made current with a second script; neither showed a dialog.
- The two disposable drawings were copies of one template and therefore had the same `FingerprintGuid`; `expectedDrawing` told them apart by file name.

## Unverified

- That a starting plugin leaves the records of other *live* Civil 3D instances untouched: no second instance was running. This is covered only by the host-free core test.
- A raised limit on a real long Civil operation (a large corridor or surface rebuild); the long write here was a blocking wait.
- Stop pressed in a desktop MCP client; the cancel was sent by the SDK client's abort signal, which is the same MCP notification.
- Several Civil 3D instances at once, and the fallback probing of skipped records.
