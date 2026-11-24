# Change list, preview and session token: live validation

Date: 2026-11-24 (Europe/Budapest). Tested commits: `b2e70dc` (round 1) and `7bc79cd` (round 2) of PR #25: the change list in the write receipt, `preview`, `changeItems`, and the session token on the local connection. Round 2 adds: DBMOD is taken back to 0 after a preview or a failed script on a drawing that was clean before and has nothing left behind; the model space draw order table counts as a container; a failed script's receipt says `changes: {available: false, reason, rolledBack}`.

## Fixture and method

**Proven:** the live host was Civil 3D 2025 (profile `C3D_Hungary.arg`) on the user's work PC. Before each plugin install the user closed every running Civil 3D; no acad.exe ran during either install. The source was checked out in a detached git worktree outside the working checkout and built there; for both commits `npm run build` and `dotnet build -c Release` reported 0 warnings and 0 errors. Plugin DLL SHA-256: `29E1E578…02AE` at `b2e70dc`, `C1C27786…E770` at `7bc79cd`.

`node scripts/install.mjs --plugin --keep-backups 0` installed each candidate after a time-stamped backup of the bundle in place (first backup: the original bundle, DLL `13AE0214…5FCC`, 9 files; second backup: the first candidate). After each install the installed DLL hash equalled the built one. The previously installed server (`eca6cdc`) stayed in place and was used for one status call only.

The fixture was a small test drawing copied to disposable files (`pr25_teszt_A.dwg`, later also `pr25_teszt_B.dwg`): 1 alignment and 1 TIN surface, both data references, 1 xref insert, 0 corridors, 0 sample lines, 267 layers. Round 1 used one instance on A; round 2 one instance on A, then a second on B. Endpoint records appeared 10 to 14 s after start, each with a 64-character token; no click was needed to load either new DLL. The test client was a scratch MCP stdio client that started the candidate server of the worktree, with `CIVIL3D_PORT` and `CIVIL3D_MCP_TOKEN` unset; the token gate was also probed with a raw socket client. Every request and reply was kept as JSON; token values were never written out.

No call asked for a save. Between the rounds the user closed the instance without saving; the copy on disk still had its source hash, size and time stamp at the start of round 2, and both copies kept their size and time stamp to the end. The working checkout stayed on `main`, clean, throughout.

## Round 1 at `b2e70dc`

| # | Case | Expected | Observed | Result |
|---|---|---|---|---|
| 1 | Status | `authorized`, `tokenRequired`, new features, drawing | `authorized` true, `tokenRequired` true, features `changeList`, `preview`, `sessionToken`, `buildMatchesServer` true. About 13 s after start `activeDrawingKnown` false; the next call true with path and fingerprint | Pass |
| 2a | Raw health without `auth` | Limited reply | `authorized` false, `tokenRequired` true, `activeDrawingKnown` false, build and features; nothing about the drawing or the operation | Pass |
| 2b, 2c | Raw read-only execute without token, with a wrong token | `CIVIL3D.UNAUTHORIZED` | Both refused in 3 to 4 ms | Pass |
| 2d | Raw read-only execute with the record's token | Runs | `result: 1` (1.8 s, first compile) | Pass |
| 3 | Status through the installed server `eca6cdc` | Information | Lists the instance with its build, `buildMatchesServer` false, a warning to update the older side, `activeDrawingKnown` false; nothing says the token is the reason | Information |
| 5 | Preview, 3 lines | `rolledBack`, nothing kept | added 3, `rolledBack` true, `complete` false with its note; counts unchanged afterwards. **DBMOD 0 → 1** | Pass, finding |
| 6 | Preview with `requireClean: true` after case 5 | Accepted if DBMOD 0 | DBMOD was 1: `CIVIL3D.DRAWING_NOT_CLEAN`, `not_started` | Finding |
| 7 | Preview: recolour a viewport, erase the xref insert | Listed; nothing kept | modified `Viewport` 1, erased `BlockReference` 1 (by name); both unchanged afterwards | Pass |
| 8 | Preview: alignment, profile from surface, 10-point TIN | The three by name, nothing kept | added 10: the three by name, 6 alignment label groups and 1 `AeccDbFeatureStyle` with a GUID name; modified: the style `Szabványos`. Lists equal the baseline afterwards | Pass |
| 9 | Preview whose script throws after adding a line | Error, nothing left | `CIVIL3D.TRANSACTION_FAILED`, `reported_error`, `committed` false, **no `changes` block**; the line does not exist | Pass, finding |
| 10 | Preview with key K1, then a real write with K1 | The real write runs | Runs and commits | Pass |
| 10 | Preview with a wrong `expectedDrawing` file name | Refused before the script | `CIVIL3D.DRAWING_MISMATCH`, `not_started`, no handle used | Pass |
| 11 | Real write: 2 lines and a circle on layer 0 | added 3, layout block under `containers` | As expected, plus **modified `DrawOrderTable` 1**; handles read back | Pass, finding |
| 12 | Recolour a line, erase the circle | modified 1, erased 1 | modified 2 (`Line`, `DrawOrderTable`), erased 1; readback right | Pass, finding |
| 13 | New layer and a line on it | `LayerTableRecord` by name, `LayerTable` under `containers` | As expected | Pass |
| 14 | `changeItems` 5, 0 and 101 | 5 items and `more` 25; counts only; refused | As expected; 101 is refused by the server's input schema and never reaches the plugin | Pass |
| 15 | Case 8 as a real write | The full list | added 10 as in the preview, names right; modified 11: the style, the drawing settings node, `DrawOrderTable`, 8 `DisplaySet`; `containers` `BlockTableRecord`, `DBDictionary` | Pass |
| 16 | Rename, then restyle an alignment that has a profile | Dependent objects | Each: modified `Alignment`, its `Profile`, `DrawOrderTable`; label groups not listed; readback right | Pass |
| 18 | 20000 lines, real and preview | `complete`, timings | Real: wall 666 ms, script 54 ms, `complete` true. Preview: wall 503 ms, script 45 ms, `rolledBack` true | Pass |

Wall time is measured in the client around the tool call, with the server already connected. Overhead (wall minus script): 2 lines and a circle about 85 ms; 20000 lines 612 ms (real) and 458 ms (preview).

Findings of round 1, each addressed in `7bc79cd`:

1. A preview left a clean drawing marked as modified (DBMOD 1). The next real write with `requireClean: true` or `backup: true`, as the working order recommends, was then refused until a save.
2. The model space draw order table appeared under `modified` on most real writes that add to or erase from model space (not on two of them), so a caller that stops on an unexpected type would stop there.
3. The receipt of a failed script had no `changes` block and did not say whether anything stayed behind.

Probes between the rounds, on the same instance:

- **P1 (outside Civil 3D, read-only).** The export table of `acdb25.dll` was read from the file, nothing was loaded. It exports `?acdbSetDbmod@@YAHPEAVAcDbDatabase@@H@Z` and `?acdbGetDbmod@@YAHPEAVAcDbDatabase@@@Z` (int forms); the long form `?acdbSetDbmod@@YAJPEAVAcDbDatabase@@J@Z` is not exported.
- **P2.** One preview whose script, beside the transaction it was given, recoloured a line through a committed `StartOpenCloseTransaction`, recoloured another through the obsolete `ObjectId.Open`/`Close` pair and added a line through a second committed open-close transaction. Neither the script guard nor the compiler refused it. The receipt listed all three (added 1, modified 2) with `rolledBack` true; the readback showed all three taken back. DBMOD was already 1, so the probe said nothing about it.
- **P3.** `throw new Exception("probe");` without a namespace compiles live and the error names `System.Exception`. The offline compile check in `tests/skills` reports `Exception` and `Entity` as ambiguous: it lacks the plugin's default aliases.
- **P4.** Among the `modified` entries of the round 1 previews, only the style `Szabványos` (case 8) belongs to an object the script did not open itself; the Civil 3D creation calls opened it.

## Round 2 at `7bc79cd`

| # | Case | Expected | Observed | Result |
|---|---|---|---|---|
| 1.1 | Status and baseline | DBMOD 0 | `buildMatchesServer` true, `authorized` true; DBMOD 0, 9 model space entities, 1 alignment, 1 surface | Pass |
| 1.2 | Preview, 3 lines | `dbmodBefore` 0, `dbmodAfter` 0 | As expected, `rolledBack` true; DBMOD query 0 | Pass |
| 1.3 | Preview: recolour a viewport, erase the xref insert | DBMOD 0, both unchanged | As expected | Pass |
| 1.4 | Preview: alignment, profile, TIN | `rolledBack`, DBMOD 0, lists equal baseline | As expected; same 10 added and the style modified as in round 1 | Pass |
| 1.5 | Preview with the three side changes of P2 on the viewport, the xref insert and a new line | All taken back, DBMOD 0 | Listed as added 1, modified 2, `rolledBack` true; colours unchanged, the line does not exist, DBMOD 0 | Pass |
| 1.6 | Preview that throws after adding a line | Error, `changes.available` false, `rolledBack` true, DBMOD 0 | `TRANSACTION_FAILED`, `reported_error`, `changes` `{available: false, reason, rolledBack: true}`, DBMOD 0 → 0; the line does not exist | Pass |
| 1.7 | Real write that throws after adding a line | `committed` false, as 1.6 | As expected, `dbmodAfter` 0, DBMOD 0 | Pass |
| 1.8 | Preview with `requireClean: true`, `backup: true`, `backupDirectory` | Accepted, no backup | Accepted (`dbmodBefore` 0), `backupPath` null, no folder or file made, DBMOD 0 | Pass |
| 1.9 | Civil 3D's own state | DBMOD 0 | DBMOD 0 read in the host; document name, `Database.Filename` and fingerprint as expected; undo recording on | Pass |
| 2.1 | Real write with `requireClean` and `backup`: 2 lines and a circle | Accepted, backup, added 3, modified 0, containers, `complete` | As expected: `containers` `BlockTableRecord` and `DrawOrderTable`, `dbmodAfter` 1. The backup copy's hash equals the file on disk | Pass |
| 2.2 | Preview on the now modified drawing | `rolledBack`, DBMOD stays 1 | `dbmodBefore` 1, `dbmodAfter` 1, DBMOD query 1 | Pass |
| 2.3 | Real write with `requireClean` | `DRAWING_NOT_CLEAN` | As expected, `not_started` | Pass |
| 2.4 | 30 lines, `changeItems` 5 | Draw order table under `containers`, modified 0 | added 30 (5 items, `more` 25), modified 0, `containers` `BlockTableRecord`, `DrawOrderTable` | Pass |
| 2.5 | Erase the circle of 2.1, recolour a line | modified 1, erased 1 | As expected, `containers` `DrawOrderTable`; readback right | Pass |
| 3.1 | Second instance on B; status | Two instances, both authorized | Both listed with their drawings, `authorized` true, no warnings; B on a free port | Pass |
| 3.2 | Read-only query to each by `expectedDrawing` | Served by the right process | A by its process (DBMOD 1), B by its process (DBMOD 0) | Pass |
| 3.3 | Raw socket: A's token to B's port and B's token to A's | `UNAUTHORIZED` twice | As expected | Pass |
| 3.4 | Preview on B, 3 lines | `rolledBack`, B DBMOD 0, A unchanged | As expected; A DBMOD 1, its handle seed unchanged | Pass |

At the end of round 2 the user looked at both windows: the file tab of B (previews only) showed no unsaved mark, the tab of A (real writes) did. This agrees with the DBMOD values read through the API.

The write journal had one line per write request of round 2 (13), each with the plugin commit, the process, `preview`, `committed`, DBMOD before and after and the counts; none for the read-only calls and none for the input the server refused.

## Saved run after the merge (`23b0fe2`)

The same afternoon, with the merged build installed on the work PC (server and plugin `23b0fe2`), one run on a fresh disposable copy covered the full working order with a save. A second instance was running on the same PC; it received only the health part of one `status` call, and the test client was pinned to the test instance's port.

| Step | Expected | Observed | Result |
|---|---|---|---|
| Start | DBMOD 0 | DBMOD 0, 9 model space entities, file equal to the source by hash | Pass |
| Preview with `requireClean`, `backup`, `backupDirectory`, `saveDrawing` | Nothing committed or saved, no backup, DBMOD 0 | `committed` false, `saved` false, `rolledBack` true, `dbmodAfter` 0; no backup file; file hash unchanged | Pass |
| The same call without `preview`: 2 lines and a circle | Committed, saved, backup of the pre-write file | `saved` true, `dbmodAfter` 0, added 3 (`Line` 2, `Circle` 1), `complete` true; the backup's hash equals the file before the call; the file on disk changed | Pass |
| Readback | The three handles on layer 0 | Present, DBMOD 0 | Pass |
| A second write with `requireClean`: 1 line | Accepted after the save | Accepted, `saved` true, `dbmodAfter` 0; the second backup equals the file after the first write | Pass |
| The user closes the test instance | No save question | None (observed by the user); file hash unchanged by closing | Pass |
| Reopen the copy | Both writes in the file, nothing of the preview | The 3 lines and the circle on layer 0, DBMOD 0; none of the preview's handles exist | Pass |

AutoCAD wrote a `.bak` next to the drawing on save. The save creates an Xrecord in memory that is not in the file after reopening; it is not in the change list because the save runs after the transaction. The draw order table appeared under `containers` for the first write and not for the second.

## Observations

- A preview, and a failed script, leave the new objects as erased stubs: their handles resolve, `IsErased` is true. Model space iteration skips them.
- Creating an alignment adds a hidden feature style with a GUID name and modifies the style `Szabványos`; the real write additionally touches 8 display sets and the drawing settings node. The preview note says that what Civil 3D does at commit is not included.
- Renaming or restyling an alignment lists its profile as modified, not its label groups.
- The installed older server sees a new plugin but gives no hint that the missing drawing is caused by the token.
- The handle seed advances on a preview; it is not taken back.
- The offline compile check in `tests/skills` reports names as ambiguous that the live executor resolves (see P3).

## Unverified

- Corridors and sample lines in the change list: the test drawing had none.
- Undo after a preview or a failed script: what the Undo list shows and what an Undo does.
- A failed commit (an error raised during commit rather than in the script).
- `CIVIL3D_MCP_REQUIRE_TOKEN=0` and `CIVIL3D_MCP_CHANGE_EVENTS=0`.
- The share of the write overhead that comes from change tracking; there was no run with tracking switched off.
- `changeItems` above 100 at the plugin level; the server refused it first.
- A call through the older server beyond `status`.
- The restore of the original plugin bundle after the test is recorded in the pull request, not here.
