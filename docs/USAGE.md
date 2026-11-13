# Using the Civil 3D MCP: the working order

This is the one place that says how an assistant should work with this server: which call comes
first, how the target drawing is identified, how a write is sent and checked, and what to do after
an error. The README and the other documents refer here and do not repeat it.

The server hands this text to the assistant by itself, so no separate skill has to be installed:

- the first part ("Working order") is sent as the server's MCP `instructions` and is the reply to
  `civil3d_skills` with `action: "guide"`;
- each later part is the reply to `action: "guide"` with the `topic` named in its marker line;
- the descriptions of `civil3d_query` and `civil3d_execute` point to the guide, because a tool
  description is the one channel every MCP client passes on.

The marker lines (`<!-- topic: name -->`) are read by the server; keep one in front of each part.
The first part must stay under 2 KB and every other part under 6 KB. The guide describes the
mechanism only. Rules that belong to an office or a project (where backups are kept, how many
attempts are allowed, how results are reported) are not part of it.

<!-- topic: core -->
## Working order

1. Before the first drawing call, call `civil3d_skills` with `action: "status"`. It runs no command
   in Civil 3D and lists every running instance: `instanceId`, plugin build, the full path and
   `fingerprintGuid` of its active drawing, and whether an operation is running
   (`operationInProgress`, `queueDepth`). Do not send a drawing call behind a running operation.
2. Identify the target drawing by full path and fingerprint and pass both as `expectedDrawing` on
   every later call. A window title or "the one I opened" is not an identification.
3. Read with `civil3d_query`. Reading needs no save and no backup. Keep queries bounded and say so
   when a result was truncated.
4. Prefer a catalog skill: `civil3d_skills` `search`, then `get`. When `run_by_name` is true, run it
   with `skill` and `params` instead of sending code.
5. Write with one `civil3d_execute` call that carries its own checks: `expectedDrawing`,
   `requireClean: true`, `backup: true`, `saveDrawing: true` when the change should reach the file,
   and a one-line `description`. The script must not save by itself (`Database.SaveAs`, `QSAVE`).
6. Read the `receipt` in the reply (`committed`, `saved`, `dbmodAfter`, `backupPath`), then read
   the changed objects back with a separate query.
7. On an error read `outcome`: `not_started` means nothing ran, `reported_error` means the
   operation ended and the receipt says whether the change went in, `unknown` means stop. Never
   resend a write whose outcome is unknown.

Details: `civil3d_skills` with `action: "guide"` and a `topic`:

- `drawing`: identifying the target drawing, and reading.
- `writing`: read before the first write. Switches, several drawings, receipt, older plugin.
- `errors`: read after any error, timeout or crash.
- `csharp`: read before sending new C#. Compile check and known API pitfalls.

<!-- topic: drawing -->
## Target drawing and reading

### Identifying the target drawing

- `civil3d_skills` with `action: "status"` gives, for each running instance, `activeDrawing` with
  `databaseFilename` (full path) and `fingerprintGuid`, the plugin build, `operationInProgress` and
  `queueDepth`. It uses the plugin's health reply and runs no command in Civil 3D, so it does not
  disturb an instance somebody else is working in.
- When `activeDrawingKnown` is false, or the action is not available (older installation), read
  `Database.Filename` and `Database.FingerprintGuid.ToString()` with one small `civil3d_query`
  sent without `expectedDrawing`. That is the only call that may omit it.
- Drawings made from the same template or copied from one file can share a fingerprint. The full
  path then tells them apart, compared case-insensitively.
- These do not identify the target: a window title, the active document seen through COM, a file
  time stamp, or the user's statement about what is open. The visible window and the MCP
  connection can belong to different instances.
- With several instances running, `expectedDrawing` selects the instance whose active drawing
  matches. A call without it is refused with `CIVIL3D.INSTANCE_SELECTION_REQUIRED`, which lists
  the candidates; pass `instanceId` for that one bootstrap query.
- The plugin compares `expectedDrawing` with the active drawing immediately before the script
  runs. A difference is `CIVIL3D.DRAWING_MISMATCH` with `outcome: "not_started"`.
- Identify again after the user switches windows or drawings, after a drawing is closed or
  reopened, and after Civil 3D restarts. Object references kept from before (an `ObjectId`, a
  cached object) are no longer valid then; look the objects up again.

### Reading

- A read needs no saved state, no `DBMOD` check and no backup. The query transaction is never
  committed. `civil3d_query` still runs the code it is given, so send only code meant to read.
- Bound each query: name the objects or the area, the fields, and a maximum count; page through
  long lists. Process large geometry inside the script and return the summary.
- A collection of more than 1000 items is cut to its first 1000 and the reply is wrapped as
  `{result, truncated: [{path, returned, total}]}`. NaN and Infinity come back as `null` with
  `{result, nonFinite: {count, paths}}`. Say so when you report such a result; a capped list was
  not fully checked.
- Do not create temporary objects in a query. The rollback removes them, but Civil 3D can
  still mark the drawing as modified, and `requireClean` then refuses the next write.
- A query reads the open drawing as it is in memory, including unsaved changes. Say whether a
  statement rests on the open drawing or on the saved file.
- Pass `timeoutSeconds` in advance for a read that is known to take long.

<!-- topic: writing -->
## Writing

A write is one `civil3d_execute` call. The call does the checks around the write itself; do not
replace them with separate calls.

| Input | Effect |
| --- | --- |
| `expectedDrawing` | Required for a write. The freshly identified full path and fingerprint. On a difference nothing runs. With several instances it selects the right one. |
| `requireClean: true` | Nothing runs on a drawing with unsaved changes (`CIVIL3D.DRAWING_NOT_CLEAN`, `not_started`). |
| `backup: true` | Before the script runs, the saved DWG file is copied to a time-stamped file, checked by size, never over an existing file. It needs a named drawing and asks for a clean one unless `requireClean: false` is set explicitly. If the copy fails nothing runs (`CIVIL3D.BACKUP_FAILED`). |
| `backupDirectory` | Absolute folder for that copy. Without it the copy goes to a per-user folder on the computer that runs Civil 3D. Ask the user where backups belong. |
| `saveDrawing: true` | The call saves the named drawing after the transaction and the document lock are closed. A read-only or unnamed drawing is refused before anything runs. |
| `description` | One line about the change. Shown on the Civil 3D command line and kept in the write journal; it is how the call is found later. |
| `timeoutSeconds` | Script time limit for this call, 1 to 1800 seconds, default 120. Set it in advance for a known long operation. |
| `idempotencyKey` | Optional. See the `errors` topic. |

Rules for the script:

- It must not save: no `Database.SaveAs`, no queued `QSAVE`. Use `saveDrawing: true`.
- `WBLOCK`, `-WBLOCK` and `Database.Wblock()` are not backups.
- It runs on the main thread, under the document lock, inside the provided `Transaction`, which
  the call commits. Do not start parallel work and do not send two writes at once.
- Check the postcondition inside the script and throw when it does not hold. The transaction is
  then rolled back, so a successful reply never hides a half-made change.
- Split a risky change with several steps (clone, move to another layer, delete) into small
  separate calls and read the state back between them.

In a series of writes to one drawing, request the backup on the first call only: every
`backup: true` makes another full copy. `requireClean` can stay on a later call only when the
previous call also saved; after a write without a save the drawing is not clean.

### Several drawings

A call acts on the drawing that is active in the selected Civil 3D instance. The switches, the
save and the receipt cover that drawing only. To change several drawings, identify all of them
first, then send one call per drawing, one after another, each with its own `expectedDrawing`,
backup and save inputs. Stop the series when one call fails or its outcome is unknown, and report
per drawing which ones were changed and saved.

### Receipt and readback

Every write that reached the drawing returns a receipt as a second text block,
`{"receipt": {...}}`; a failed or refused write has it too, also in `structuredContent.receipt`.
Fields: `drawing` (`databaseFilename`, `fingerprintGuid`), `dbmodBefore`, `dbmodAfter`,
`committed`, `saved`, `fileLastWriteUtc`, `fileBytes`, `backupPath`.

- `committed` says whether the change is in the open drawing, `saved` whether this call wrote the
  file. Take the save state (`saved: true`, `dbmodAfter: 0`) and the backup location from the
  receipt, not from a separate `DBMOD` query.
- After a failed save the receipt shows `committed: true`, `saved: false`: the change is in the
  open drawing and not in the file.
- The receipt, a file size and a clean state do not prove that the geometry is right. Read the
  result back independently with a targeted query: the requested objects, counts, extents, and
  that objects outside the scope are unchanged. If that is not possible, report the check as
  missing.
- Do not automatically repeat a readback method that hung before.

### Older plugin

`CIVIL3D.PLUGIN_UPDATE_REQUIRED` (`not_started`) means the running plugin predates `requireClean`,
`backup` and `backupDirectory`. Do not silently drop the inputs. Tell the user, and until the
plugin is updated provide the same guarantees by hand: a read-only query of
`Application.GetSystemVariable("DBMOD")` before the write, an unchanged file copy checked by size
before the first write, and another `DBMOD` check after the save. Do not start writing when the
file is locked or the copy cannot be verified.

<!-- topic: errors -->
## Errors, time limits and uncertain outcomes

An error reply has `isError: true`, a text message and `structuredContent` with schema
`civil3d-mcp-error/v1`: `code`, `category`, `message`, `source`, `outcome`, `retryable`. The
server never repeats a call by itself.

| `outcome` | Meaning | What to do |
| --- | --- | --- |
| `not_started` | Nothing ran. | Fix the cause (target drawing, clean state, an input), then send again. |
| `reported_error` | The plugin ended the operation and reported it. | The receipt's `committed` says whether the change went in. |
| `unknown` | The outcome is not known. | Stop and reconcile as described below. Do not resend. |

### Time limits

- `CIVIL3D.TIMEOUT` is the plugin's own script limit (120 s, or `timeoutSeconds`). It is raised
  before any commit, so it is `reported_error` and the drawing is unchanged. The same call may be
  sent again with a larger `timeoutSeconds`, unless the user asked for a single attempt.
- `CIVIL3D.COMMAND_TIMEOUT` (the server stopped waiting), a connection lost after sending
  (`CIVIL3D.TRANSPORT_ERROR`), a reply too large to return (`CIVIL3D.RESPONSE_TOO_LARGE`) and
  `CIVIL3D.CANCELLED` after sending are `unknown`: the operation may still be running or may have
  committed. The MCP client's own tool-call timeout gives no error object at all; treat it the
  same way.
- `CIVIL3D.COMMAND_CONTEXT_TIMEOUT` means Civil 3D did not start the command within 15 seconds,
  for example because a dialog is open or a command is active. Nothing ran. Ask the user to
  bring Civil 3D to an idle state.

### Uncertain outcome

1. Stop: no write, no save, no retry. A timeout, a responsive Civil 3D window or the Esc key
   proves neither a rollback nor a completion.
2. Call `civil3d_skills` with `action: "status"`. It works outside the drawing execution queue
   and shows whether an operation is still running (`operationInProgress`, `queueDepth`). Do not
   queue drawing calls behind it. If the action is unavailable, say so; do not replace it with a
   new C# call.
3. Once the operation has ended, the write journal on the computer that runs Civil 3D
   (`%LOCALAPPDATA%\Civil3dMcpRoslyn\journal\<year>-<month>.jsonl`) has one line per write
   request with `timeUtc`, `description`, `ok`, `errorCode`, `committed`, `saved` and
   `backupPath`. Reading it is not a drawing call. Ask the user for it if you cannot read files
   on that computer.
4. Identify the drawing again and compare the real objects and the saved state with what the
   write intended. Only then decide whether anything has to be sent again.
5. An `idempotencyKey` (1 to 128 letters, digits, `.`, `_`, `:`, `-`) makes the plugin refuse the
   same write twice within one Civil 3D session: `CIVIL3D.IDEMPOTENCY_COMPLETED`,
   `CIVIL3D.IDEMPOTENCY_IN_PROGRESS` or `CIVIL3D.IDEMPOTENCY_CONFLICT`, all `not_started`. It
   keeps no result and does not survive a restart, so it does not replace step 4.
6. While the state is uncertain, do not reload the plugin, restart Civil 3D, or overwrite or
   restore files. Restore from the unchanged backup only when damage is proven or the user asks.
   Evidence from outside the MCP can help the comparison but does not close a pending operation.

### After a crash of Civil 3D

Send no further drawing call. Keep the error text and the data of the last save and backup.
Every earlier `instanceId`, document and object reference is stale. Continue only after the user
has reopened the drawing (or the recovered file), with a fresh `status`, path, fingerprint and
`DBMOD`. Check what remains of the interrupted operation and do not replay it automatically.

### Common error codes

| Code | Outcome | Meaning and next step |
| --- | --- | --- |
| `CIVIL3D.CONNECTION_FAILED`, `CIVIL3D.CONNECTION_TIMEOUT` | `not_started` | No Civil 3D with the plugin answered. Ask the user to start Civil 3D and load the plugin. |
| `CIVIL3D.INSTANCE_SELECTION_REQUIRED` | `not_started` | Several instances and no `expectedDrawing`. Pass `expectedDrawing`, or `instanceId` for the bootstrap query. |
| `CIVIL3D.DRAWING_GUARD_REQUIRED` | `not_started` | A write was sent without `expectedDrawing`. |
| `CIVIL3D.DRAWING_MISMATCH` | `not_started` | Another drawing is active. Identify again; do not guess. |
| `CIVIL3D.DRAWING_NOT_CLEAN` | `not_started` | Unsaved changes; a freshly opened drawing can count as modified too. Find out where they come from. Do not save them or drop the switch silently. |
| `CIVIL3D.DRAWING_READ_ONLY`, `CIVIL3D.SAVE_PATH_REQUIRED` | `not_started` | The file cannot be saved: open read-only, or never named. The user has to fix it. |
| `CIVIL3D.BACKUP_FAILED` | `not_started` | The copy could not be made or verified. Fix the folder; do not write without the copy. |
| `CIVIL3D.PLUGIN_UPDATE_REQUIRED` | `not_started` | Older plugin; see the `writing` topic. |
| `CIVIL3D.COMPILATION_ERROR` | `reported_error` | The script did not start. Fix it from the diagnostics; see the `csharp` topic. |
| `CIVIL3D.SANDBOX_VIOLATION` | `reported_error` | The script text contains something the plugin blocks; it was refused before compiling. |
| `CIVIL3D.TRANSACTION_FAILED` | `reported_error` | The script threw or the commit failed; the change was rolled back (`committed: false` in the receipt). |
| `CIVIL3D.RESULT_SERIALIZATION_FAILED` | `reported_error` | The return value could not be turned into JSON. This happens before the commit; confirm with `committed` in the receipt, then return simpler values. |
| `CIVIL3D.SAVE_FAILED` | `reported_error` | Committed in the open drawing, not saved. Do not repeat the change; resolve the save with the user. |

For a fix, change one thing with a stated reason and a checkable result. Do not send the same
failed idea again in another wrapping without new evidence.

<!-- topic: csharp -->
## Writing C# for the scripts

### What a script is

A script is a method body. It has the variables `Document`, `CivilDoc`, `Database`, `Transaction`
and `Editor`, and returns its result with `return`. The common AutoCAD and Civil 3D namespaces
are imported. Keep it synchronous: a script that uses `await` is rejected.

- Returnable: primitives, strings, `Guid`, dates and times, anonymous objects and classes or
  records declared in the script, arrays, lists and other sequences (LINQ included), tuples,
  `Dictionary<string, ...>`, `ObjectId`, `Handle`, `Point2d/3d`, `Vector2d/3d`. Enums come back
  as numbers; use `.ToString()` for names.
- Not returnable: `DBObject` instances (entities, Civil objects). Return their fields.
- Limits: 1000 items per collection, depth 8, 10000 nodes in total. Do not work around them;
  summarize or page instead.
- Names that exist twice have a default: `DBObject`, `Entity` and `ErrorStatus` are the AutoCAD
  types, `Surface` and `Section` the Civil 3D ones, `Exception` is `System.Exception`. Write the
  full name for the other one, and for other duplicated names such as `Table`.
- Other namespaces need a `using` line or a full name, for example
  `Autodesk.Civil.DatabaseServices.Styles`.

### Before sending new code

- Prefer a catalog skill to new code. An unchanged skill needs no extra check.
- For an API member you are not sure of, use `civil3d_skills` with `action: "api_lookup"`. It
  searches the public metadata of the loaded Civil 3D assemblies and runs nothing. Do not dump
  whole assemblies or objects.
- A compile error (`CIVIL3D.COMPILATION_ERROR`) is reported before anything runs, for a write
  as well. Fix it from the diagnostics and send again.
- A development checkout of this repository can compile a code body without Civil 3D:
  `dotnet run --project tests/skills/Civil3dMcp.SkillTests.csproj --configuration Release -- --code <file.cs>`
  (exit code 0 compiled, 1 compile errors, 2 bad arguments or missing Autodesk references). It
  needs the Autodesk reference assemblies and is not part of an installed server.
- A successful compile proves neither run-time behaviour nor geometry, and it is not a
  permission to write.

### Known API pitfalls

Checked against the Civil 3D 2025 assemblies:

| Symptom | Cause | Do this |
| --- | --- | --- |
| `SurfaceStyle` is not found | The type is in `Autodesk.Civil.DatabaseServices.Styles`. | Add the `using` line or write the full name. |
| `foreach` does not compile on a surface definition collection | `SurfaceDefinitionBreaklines` and `SurfaceDefinitionDrawingObjects` have no enumerator. | Loop with `.Count` and `[i]`. |
| The `FINGERPRINTGUID` system variable cannot be read | The fingerprint is a database property. | Use `Database.FingerprintGuid`. |
| A returned Civil object is refused | `DBObject` instances are not returnable. | Return the fields you need. |

Reported from field use, not covered by this repository's tests; treat them as warnings:

- Reading `AppliedSubassembly.Parameters` where a parameter is overridden for a station range has
  crashed Civil 3D. Do not read such values through the API; ask the user or have them read it
  in the user interface.
- After a TIN surface is edited, vertex, edge and triangle objects kept from before are stale.
  Find them again from the new state; an equal index or XY position is not identity.
- Cloning objects from another database and moving the clones to another layer in the same
  transaction has failed with `eNoDatabase`. Use separate calls and read back in between.
- `Assembly.AddSubassembly` does not re-attach an existing subassembly. After copying, check the
  new object, its attachment point and the rebuilt result separately.
- The COM and the .NET interfaces do not expose the same properties as writable. Check with
  `api_lookup` that the setter exists before planning a write around it.
