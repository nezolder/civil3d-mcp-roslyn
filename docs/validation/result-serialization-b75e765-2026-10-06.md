# Result serialization: Civil 3D 2025 validation

Date: 2026-10-06. Tested source: `b75e76512d748e7f138ca9cc16174bbb8fc84fe5` on `claude/kind-volta-rsvbm5`.

**Proven:** all requested live query cases passed in Civil 3D 2025 Hungary, including LINQ enumeration over two real Civil alignment objects without `.ToList()`. The new plugin remains installed; the previous version was not restored.

## Build and offline checks

- The fetched branch head and clean test checkout both matched the exact commit above.
- A clean lockfile installation with lifecycle scripts disabled succeeded.
- The plugin Release build used real local AutoCAD/Civil 3D 2025 reference assemblies: zero errors and zero warnings.
- `npm run test:plugin` passed all 104 .NET cases and the Node tool-contract test. The added cases cover LINQ, dates/times, tuples, script-defined types, truncation, endless/failing lazy sequences, lazy-loop timeout and retained unsupported-value rejection.
- Candidate and configured local Node entry points built successfully. The local integration source tree matched the tested commit.

## Live setup and results

The candidate DLL hash was verified after installation, and its loaded module identity was checked inside Civil. The test instance was launched with the user's actual Civil 3D 2025 Hungary shortcut settings and `<<C3D_Hungary>>` profile.

A disposable filesystem copy of the stock Hungary template was backed up unchanged. Two synthetic alignments, with lengths 100 and 150 drawing units, were created only in this fixture using `civil3d_execute` with `saveDrawing: true`. The write used the verified full drawing filename/fingerprint and instance guards. A separate materialized query verified both alignment names and lengths, and independent readback confirmed saved state and `DBMOD=0`. A second unchanged filesystem backup captured the populated fixture before the requested read probes.

Every query below used the same identified Civil process and guarded populated fixture:

| Submitted code or named query | Observed result |
| --- | --- |
| `return CivilDoc.GetAlignmentIds().Cast<ObjectId>().Select(id => ((Alignment)Transaction.GetObject(id, OpenMode.ForRead)).Name);` | An array with both expected synthetic alignment names, matching the independent materialized inventory. No `.ToList()` was submitted. |
| `return DateTime.Now;` | An ISO 8601 string with seven fractional-second digits and a timezone offset; it parsed as the current time. |
| `return (1, "a");` | `[1,"a"]` |
| `record ProbeRecord(int Count, string Name);` followed by `return new ProbeRecord(7, "test");` | `{"count":7,"name":"test"}` |
| `return Enumerable.Range(0, 1500);` | A wrapper with `result` containing exactly 1,000 integers, `0` through `999`, in order, and the truncation metadata below. All returned elements were compared with the expected values. |
| `skill: "drawing_info"`, `params: {}`, no submitted code | Succeeded, reported the expected fixture identity and `DBMOD=0`. |
| Follow-up `return 1;` | Returned `1`. |

The lazy range's exact truncation metadata was:

```json
[{"path":"$","returned":1000,"total":null}]
```

`total` is `null` because the serializer does not exhaust a lazy sequence to determine its full length.

All requested query cases were repeated successfully through a second, distinct fresh Node MCP server process using the rebuilt configured entry point. Both servers advertised the updated return-value guidance and exactly the same three public tools. Final independent identity/health checks showed all documents saved, `DBMOD=0`, no operation in progress and queue depth zero. The populated fixture's file hash was unchanged throughout the read probes, and the test instance closed normally.

The user's saved work drawing was then reopened with the original Hungary shortcut settings. An identity-only guarded query verified the new plugin, the expected drawing filename/fingerprint, saved state, `DBMOD=0` and idle health. Its file hash remained unchanged. No serialization test, edit or save was performed on that work drawing.

## Dependency advisory and limits

`npm audit` reported one critical advisory for the existing indirect dependency `proxy-addr@2.0.7`: [GHSA-jqcg-44mw-7w3h](https://github.com/advisories/GHSA-jqcg-44mw-7w3h), concerning HTTP proxy trust-subnet handling. The published fix is `2.0.8`. The tested lockfile was left unchanged; the dependency update is a separate follow-up. The configured repository entry point uses stdio and does not create an Express HTTP server. Exposure through other consumers was not evaluated.

**Proven** MCP restart evidence concerns fresh SDK clients and server processes. Already-open desktop clients were not forcibly restarted; they need reconnect/restart to refresh cached tool descriptions. The new plugin is active in the restored Hungary Civil instance.

**Unverified:** other Civil versions, arbitrary custom types/getters, general performance changes and broader production drawings. Lazy-result timeout behavior was covered offline; this run did not add a live runaway-loop test. The synthetic fixture setup and query probes do not extend engineering-recipe validation scopes.

Local paths, work-drawing names, client data, proprietary assemblies, private logs and development history are excluded from this report.
