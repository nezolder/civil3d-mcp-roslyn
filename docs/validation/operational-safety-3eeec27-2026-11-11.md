# Operational safety from the architecture review: live validation

Date: 2026-11-11 (Europe/Budapest). Tested commit: `3eeec27` (command-free instance choice, write safety switches, receipt, journal, versions, install script, stale record check by program name).

## Fixture and method

**Proven:** the live host was Civil 3D 2025 (profile `<<C3D_Hungary>>`) on the user's work PC, regional setting hu-HU. The user closed the one Civil 3D that was open, so no other instance was running. The source was checked out at `3eeec27` in a git worktree outside the working checkout and built there (TypeScript build and `dotnet build -c Release`, 0 warnings, 0 errors, against the reference assemblies of the local installation).

Both halves of the new install script were used for the setup:

- `node scripts/install.mjs --server --target <temp folder>` copied the server (93 runtime packages). The test client started the server from that copy, not from the worktree.
- `node scripts/install.mjs --plugin` backed up the installed bundle and copied the built plugin into it; the installed DLL hash then equalled the built one.

Two instances were started on two disposable copies of one test drawing (`pr16_teszt_A.dwg`, `pr16_teszt_B.dwg`). Instance A took port 8080, instance B a free port. Their endpoint records appeared 12 s and 15 s after start. The Node server was driven over stdio by a scratch MCP client with `CIVIL3D_PORT` unset, so discovery had to choose between two instances. No work drawing was opened. Writes only created layers named `MCP_PR16_T<n>`.

The two copies share one `FingerprintGuid`, so `expectedDrawing` told them apart by file name only.

After the run the user closed both instances without saving, and the previous bundle was copied back from the install script's backup and verified file by file by hash (11 files, no mismatch). The working checkout stayed on `main`, clean, throughout.

## Host-free tests on the same machine

Under hu-HU, at `3eeec27`: error suite 21 of 21, plugin suites 126 C# cases and 4 Node tests, skills suites 37 Node tests and the C# checks (42 templates compiled), transport 19 Node tests and 7 C# cases.

A first run of the transport tests at `49ead2f`, while the user's Civil 3D was still open, failed 7 tests because discovery also tried the default port 8080 and reached that instance (health probes and read-only `return 1;` scripts; no write). `3eeec27` adds `CIVIL3D_MCP_DEFAULT_PORT_FALLBACK=0`, which the routing tests set; the rerun with the same Civil 3D still open passed and its log no longer named the live instance.

## Live results

| Case | Expected | Observed |
|---|---|---|
| Status action | Server and both plugins report the same commit; both drawings known; features listed | Server and both plugins at `3eeec27`, `buildMatchesServer` true, `activeDrawingKnown` true with the A and B paths, features `activeDrawing`, `writeSafety`, no warnings (47 ms) |
| Query with `expectedDrawing` of A, then of B | Each served by the right instance, no identity command in either | Served by the right process each time. Host debug log over all runs: 0 `getActiveDrawingIdentity` |
| Query with no target | `CIVIL3D.INSTANCE_SELECTION_REQUIRED` naming both drawings, nothing run | As expected, outcome `not_started`, 32 ms |
| Write with `requireClean: true` on a saved drawing | Success with a receipt | `dbmodBefore` 0, `dbmodAfter` 1, `committed` true, `saved` false |
| Command-line text | The description is shown | `LASTPROMPT` read by the same script: `[Civil 3D MCP] PR16 T4: create layer MCP_PR16_T4 (requireClean, no save)` |
| The same write again with `requireClean: true` | `CIVIL3D.DRAWING_NOT_CLEAN`, nothing run | As expected, outcome `not_started`, receipt with `committed` false; the layer did not exist afterwards |
| Write with `saveDrawing: true` | `saved` true, file facts match the disk | `DBMOD` 1 before, **0 after**; `fileBytes` and `fileLastWriteUtc` equal to the file on disk |
| `requireClean: true` directly after a saved write | Depends on `DBMOD` after a host-managed save | Accepted (`dbmodBefore` 0). The switch can be used on consecutive saved writes |
| `backup: true` with `saveDrawing: true` | Copy of the pre-write file in the default folder | Copy under `%LOCALAPPDATA%\Civil3dMcpRoslyn\backups`, its hash equal to the file's hash before the call; the drawing file changed afterwards. Civil 3D holding the drawing open did not prevent the copy |
| `backup: true` with `backupDirectory` | Copy in the given folder | As expected; the folder was created |
| `backup: true` on a drawing with unsaved changes | `CIVIL3D.DRAWING_NOT_CLEAN`, no copy | As expected, no new file |
| Relative `backupDirectory` | `CIVIL3D.INVALID_INPUT`, nothing run | As expected; no layer, no copy, no folder. Outcome `reported_error`, as for every input the plugin validates |
| Write to B by `expectedDrawing` (same fingerprint as A) | Lands in B only | The layer exists in B and not in A |
| Script that throws after creating a layer | Error with a receipt, nothing committed | `CIVIL3D.TRANSACTION_FAILED`, receipt `committed` false, layer absent |
| Journal | One line per write request, from both instances, no code | 11 lines in `journal\2026-11.jsonl` (10 from A, 1 from B) with description, code hash and size, `ok`, `errorCode`, `committed`, `saveRequested`, `saved`; no code text |
| Plugin install while Civil 3D runs | Refusal, nothing changed | "The installed plugin is in use (EBUSY). A running Civil 3D has it loaded. … Nothing was changed.", exit code 1, hash unchanged, no new backup |
| Stale record whose process id belongs to another program | Removed by a starting plugin | A planted record (process id of a system service, no program name) was gone when instance A registered |
| Drawing switch | The remembered drawing follows without a command | The user switched instance B to a new drawing and back three times. Each of the six changes was visible to a status poll within 0.2 to 0.5 s of the plugin's own observation time; instance A stayed unchanged. No command was sent |
| Instance closing | Leaves the list cleanly | Each instance stayed listed with its drawing until it disappeared between two polls (0.5 s apart), with no error in between; both removed their own endpoint records. With none left, status returned an empty list and the warning that no running Civil 3D was found |

Timings: first query in a fresh instance 2.8 s (A) and 1.6 s (B); first save 2.9 s, later saves 0.6 to 1.2 s.

## Observations

- Instance B showed `DBMOD` 1 right after the drawing was opened, before any call; instance A showed 0. The cause was not looked into. A `requireClean` write to such a drawing is refused until it is saved, which is the intended reading of the switch, but a caller may meet it on a drawing nobody touched.
- A caller that passed only `backup: true` to a drawing with unsaved changes was told to repeat the call "without requireClean". The text was corrected after the test (commit `676976e`): it now names `requireClean: false`. That change is covered by the plugin core tests only.
- The plugin half of the install script copies the top-level files of the build output, as the manual installs before it did. Language folders are not copied and older DLLs lying in the bundle are left alone.

No click was needed to start the two instances on this machine.

## Unverified

- Three or more instances, and an instance of an older plugin next to a new one (the command fallback); both are covered by the Node routing tests only.
- A backup of a drawing on a network drive, and of a drawing large enough for the copy to take noticeable time.
- The journal under real concurrency (two instances writing in the same moment); the test wrote from the two instances one after the other.
