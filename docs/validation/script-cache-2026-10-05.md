# Script cache validation — Civil 3D 2025

Date: 2026-10-05 (Europe/Budapest)

- Tested branch: `claude/kind-volta-rsvbm5`.
- Tested implementation: `8eda40a8373697fe29f18ca8ec415d793558abae`.
- Baseline: its parent, `f4a301a6946482861a0b5c57fa07ce1cd43809ba`.
- **Proven:** the requested build, plugin tests, repeated live query, repeated compilation error, and subsequent successful query all passed. No implementation failure was found in this scope.

## Build and automated tests

**Proven:** both plugin versions built in Release for `net8.0-windows` against genuine locally installed Autodesk assemblies. All five reference files were independently SHA-256 compared with the installed files and matched:

| Assembly | Version |
| --- | --- |
| accoremgd, AcDbMgd, acmgd | 25.0.0.0 |
| AecBaseMgd | 8.7.49.0 |
| AeccDbMgd | 13.7.0.1458 |

Toolchain: .NET SDK 10.0.401, Node v24.17.0, npm 12.0.2. The live host was Civil 3D 2025 Hungary.

Build invocation, with the machine-specific reference directory omitted:

```text
dotnet build plugin/Civil3dMcpPlugin/Civil3dMcpPlugin.csproj --configuration Release -p:Civil3DReferencesPath=<local-reference-directory>
npm ci --ignore-scripts
npm run test:plugin
```

A writable local npm cache was supplied during installation. Clean dependency installation succeeded.

Candidate result: **72 .NET PASS lines, plus 1 Node test passed; 0 failed.** This includes the previous core/modal/serializer coverage and all six new cache checks:

```text
PASS script cache returns only the script stored for the exact code
PASS script cache distinguishes codes whose 32-bit string hashes collide
PASS script cache evicts the least recently used script at capacity
PASS script cache replaces an existing entry without growing
PASS script cache clear removes every entry
PASS script cache rejects a non-positive capacity
```

Both Release builds finished with 0 errors. Each build emitted two occurrences of warning `NU1900`: NuGet could not retrieve package vulnerability data from its service index. **Unverified:** the NuGet dependency vulnerability audit. This warning did not prevent compilation or the live probes.

## Live verification

**Proven:** each version ran in a separate fresh Civil process with a new empty drawing from the same stock template. The loaded plugin's in-process MVID and assembly location matched that version's built and installed DLL. Both versions used the same Node/MCP client from the tested branch.

All calls went through the actual `civil3d_query` MCP tool. Private request metadata enabled the existing internal cache/compilation measurements without changing the public tool arguments. Each process exposed exactly the three established public tools.

The sequence was: identity/bootstrap; identical good query twice; identical invalid query twice; a different, previously uncompiled good query and its repeat; ten warm repetitions of the first good query; final independent query and idle-health check. There were 18 query calls per version, including the two intentionally failing calls.

| Case | Baseline | Candidate |
| --- | --- | --- |
| First good query | Success; 1 miss, 1 compilation | Success; 1 miss, 1 compilation |
| Exact good-query repeat | Same result; 1 hit, 0 compilations | Same result; 1 hit, 0 compilations |
| First invalid query | Expected compilation error; 1 miss, 1 failed compilation | Same error; 1 miss, 1 failed compilation |
| Exact invalid-query repeat | Same error; cached script, 0 new compilations | Same error; 1 miss, 1 failed compilation |
| New good query after errors | Success; 1 compilation | Success; 1 compilation |
| Repeat of recovery query | Success; 1 hit | Success; 1 hit |
| Final drawing state | DBMOD=0, empty, identity unchanged | DBMOD=0, empty, identity unchanged |
| Final plugin state | Idle, queue depth 0 | Idle, queue depth 0 |

The first good script returned `value=42` and `dbmod=0`; the newly compiled recovery script returned `value=43` and `dbmod=0`.

The intentional invalid script was:

```csharp
return CacheProbeMissingSymbol_20261005;
```

All four failures, including both versions, returned the identical `CIVIL3D.COMPILATION_ERROR` message:

```text
C# compilation failed:
(1,8): error CS0103: The name 'CacheProbeMissingSymbol_20261005' does not exist in the current context
```

This directly confirms that the candidate does not retain failed compilations, preserves the reported diagnostic, and accepts successful compilations after an error.

Independent COM readback also confirmed one unchanged, saved/clean, idle test document per process before normal application exit. No drawing writes or saves were issued. The original installed plugin bundle was restored afterward and its complete file set was hash-verified.

## Short warm-cache comparison

**Proven in this measurement:** ten sequential warm repetitions per version, with identical query text and Node client, after the same probe sequence.

| Metric | Baseline | Candidate |
| --- | ---: | ---: |
| Warm cache hits | 10/10 | 10/10 |
| Warm compilation attempts | 0 | 0 |
| Median internal execution time | 10.868 ms | 3.167 ms |
| Median Node-to-plugin end-to-end time | 45.854 ms | 9.113 ms |

These are scoped summary measurements; raw traces and machine-specific receipts remain private. The warm hit count is unchanged. The candidate was faster in this run.

**Probable:** skipping repeated options/reference construction on cache hits contributes to the reduced execution time, consistent with the implementation change.

**Unverified:** a general speedup or long-session memory behavior. This is one host, one process per version, ten warm samples, baseline first; command scheduling and host state can affect latency. The six cache unit tests are host-independent, and the live sequence was read-only on an empty fixture. It does not establish production authoring/save behavior or prolonged stress performance.

Only this validation report is added to the branch; the tested implementation was not edited.

## Follow-up: dependency vulnerability audit

The `NU1900` gap above was closed separately on 2026-10-05 from a Linux environment with access to the NuGet service index (.NET SDK 8.0.131), against commit `08a603c`.

```text
dotnet list <project> package --vulnerable --include-transitive
```

**Proven:** `Civil3dMcpPlugin`, `Civil3dMcpPlugin.CoreTests`, `Civil3dMcpPlugin.TransportTests` and `Civil3dMcp.SkillTests` report no vulnerable direct or transitive packages from `https://api.nuget.org/v3/index.json`. `npm audit` on the Node server reports 0 vulnerabilities.
