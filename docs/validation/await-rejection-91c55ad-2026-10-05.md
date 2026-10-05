# Await rejection: Civil 3D 2025 validation

Date: 2026-10-05. Tested source: `91c55add94c41715e327bae53c0555010fffb664` on `claude/kind-volta-rsvbm5`.

**Proven:** the requested script is rejected with `CIVIL3D.COMPILATION_ERROR` and `MCP0001` in live Civil 3D 2025 Hungary. The next normal query and the named `drawing_info` recipe succeed. The new plugin remains installed; the previous DLL was not restored.

## Build and offline checks

- A clean checkout was pinned to the exact commit above.
- The plugin Release build used the real local AutoCAD/Civil 3D 2025 reference assemblies and completed with zero errors.
- `npm run test:plugin` passed all 95 .NET cases and the Node tool-contract test. This includes await rejection with its source position and the allowed synchronous identifier named `await`.
- The local integration source was updated to this feature commit, and `npm run build` passed for the configured Node MCP entry point.
- NuGet emitted `NU1900` warnings because vulnerability advisory data could not be retrieved. These successful build/tests are not a dependency-security audit.

## Live checks

The installed DLL hash matched the candidate build. Its loaded module identity was checked inside a fresh Civil process started with the user's actual Civil 3D 2025 Hungary shortcut settings and `<<C3D_Hungary>>` profile.

Error tests ran on a disposable filesystem copy of the stock Hungary template. Full drawing filename and fingerprint were checked, and subsequent calls used the drawing and instance guards. An unchanged filesystem backup was retained. No development test modified a work-project drawing.

| Check | Observed result |
| --- | --- |
| `civil3d_query` with `await System.Threading.Tasks.Task.Delay(100); return 1;` | Prompt error response with `CIVIL3D.COMPILATION_ERROR`, `MCP0001` and position `(1,1)`; no timeout or Civil restart was needed. |
| Subsequent `civil3d_query` with `return 1;` | Returned `1` in the same Civil process. |
| Named query with `skill: "drawing_info"`, `params: {}` and no submitted code | Succeeded and returned the guarded fixture's identity with `DBMOD=0`. |
| Independent identity/health readback | `DBMOD=0`, all documents saved, no operation in progress and queue depth zero. |
| Fresh MCP server restart | A second distinct Node server process loaded the rebuilt configured entry point, advertised the updated await guidance and the same three public tools; normal query and named recipe passed again. |
| Fixture integrity and closure | The fixture's file hash was unchanged; the saved test instance closed normally. |

The returned diagnostic was:

```text
C# compilation failed:
(1,1): error MCP0001: 'await' is not supported. Scripts run synchronously on the Civil 3D main thread, where awaiting can deadlock the application; call the synchronous API instead.
```

After testing, the user's saved work drawing was reopened with the original Hungary shortcut settings. An identity-only guarded query confirmed the new plugin, the expected filename/fingerprint, `DBMOD=0`, saved state and idle health. Its file hash remained unchanged. No error probe, edit or save was performed on that drawing.

## Limits

**Proven** MCP restart evidence concerns freshly launched SDK clients and the configured Node entry point. Already-open desktop clients were not forcibly restarted; they still need reconnect/restart to refresh cached tool descriptions. The new plugin's await protection is active in the restored Hungary Civil instance.

**Unverified:** other Civil versions, arbitrary asynchronous patterns beyond the covered checks, general performance changes and live write/save behavior for this feature. These results do not extend earlier engineering-recipe validation scopes.

Machine paths, work-drawing names, client data, proprietary assemblies, private logs and development history are excluded from this report.
