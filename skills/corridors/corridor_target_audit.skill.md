---
name: corridor_target_audit
category: corridors
description: Audit target mappings for one corridor handle with optional baseline and region name filters
requires_write: false
aliases: ["nyomterv targetkapcsolatok auditja", "corridor target mapping audit by baseline and region", "corridor target object mapping review"]
workflow_tags: ["audit"]
tested_civil_version: "2025"
validation_summary: "Offline + live: empty corridor with one baseline, DBMOD unchanged. Populated target mappings and self-surface dependencies unverified."
parameters:
  - name: corridorHandle
    type: string
    required: true
    description: Hex handle of one existing Corridor
  - name: baselineNameFilter
    type: string
    required: false
    description: Optional exact baseline name filter
  - name: regionNameFilter
    type: string
    required: false
    description: Optional exact region name filter within matching baselines
  - name: targetLimit
    type: int
    required: false
    description: Maximum target mappings to return (default 100, 1-100)
  - name: targetObjectLimit
    type: int
    required: false
    description: Maximum mapped object IDs inspected per mapping (default 20, 1-25)
---

## Code Template

```csharp
string corridorHandle = "CORRIDOR_HANDLE";
string baselineNameFilter = "";
string regionNameFilter = "";
int targetLimit = 100;
int targetObjectLimit = 20;

if (string.IsNullOrWhiteSpace(corridorHandle)
    || !System.Text.RegularExpressions.Regex.IsMatch(corridorHandle, "^[0-9A-Fa-f]{1,16}$")
    || !long.TryParse(corridorHandle, System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture, out var parsedHandle) || parsedHandle <= 0)
    return new { success = false, error = "corridorHandle must be a positive hexadecimal handle." };
if (targetLimit < 1 || targetLimit > 100 || targetObjectLimit < 1 || targetObjectLimit > 25)
    return new { success = false, error = "targetLimit must be 1-100 and targetObjectLimit must be 1-25." };
if (baselineNameFilter == null || regionNameFilter == null
    || baselineNameFilter.Length > 256 || regionNameFilter.Length > 256)
    return new { success = false, error = "Name filters must be strings no longer than 256 characters." };

// Host access starts here.
ObjectId corridorId;
try
{
    corridorId = Database.GetObjectId(false, new Handle(System.Convert.ToInt64(corridorHandle, 16)), 0);
}
catch
{
    return new { success = false, error = "corridorHandle does not resolve to an object in this drawing.", corridorHandle };
}
if (corridorId.IsNull)
    return new { success = false, error = "corridorHandle does not resolve to an object in this drawing.", corridorHandle };

Corridor corridor;
try
{
    corridor = Transaction.GetObject(corridorId, OpenMode.ForRead) as Corridor;
}
catch (System.Exception error)
{
    return new { success = false, error = "corridorHandle could not be opened in this drawing.", errorType = error.GetType().Name, corridorHandle };
}
if (corridor == null || corridor.IsErased)
    return new { success = false, error = "corridorHandle must identify an existing Corridor.", corridorHandle };

var ownSurfaceIds = new HashSet<ObjectId>();
var ownSurfaceCount = 0;
var selfSurfaceCheckStatus = "complete";
try
{
    var ownSurfaces = corridor.CorridorSurfaces;
    ownSurfaceCount = ownSurfaces.Count;
    foreach (CorridorSurface ownSurface in ownSurfaces)
    {
        if (!ownSurface.SurfaceId.IsNull)
            ownSurfaceIds.Add(ownSurface.SurfaceId);
    }
}
catch (System.Exception error)
{
    selfSurfaceCheckStatus = "unknown";
}

var targetMappings = new List<object>();
var diagnostics = new List<object>();
var warnings = new List<object>();
var diagnosticCount = 0;
var warningCount = 0;
var targetMappingTotal = 0;
var targetIdsInReturnedMappings = 0;
var targetIdsInspectedInReturnedMappings = 0;
var targetObjectOpenErrorCount = 0;
var targetEnumerationErrorCount = 0;
var warningCounts = new Dictionary<string, int>(StringComparer.Ordinal);

Action<string, string, string> addWarning = (code, baselineName, regionName) =>
{
    warningCount++;
    warningCounts[code] = warningCounts.TryGetValue(code, out var previous) ? previous + 1 : 1;
    if (warnings.Count < 50)
        warnings.Add(new { code, baselineName, regionName });
};

Action<string, string, string, System.Exception> addDiagnostic = (code, baselineName, regionName, error) =>
{
    diagnosticCount++;
    if (diagnostics.Count < 50)
        diagnostics.Add(new { code, baselineName, regionName, errorType = error?.GetType().Name });
};

if (selfSurfaceCheckStatus == "unknown")
    addWarning("CORRIDOR_SURFACE_SCAN_FAILED", corridor.Name, null);

var baselineTotal = corridor.Baselines.Count;
var baselineMatched = 0;
var regionsInMatchedBaselines = 0;
var regionMatched = 0;
var matchingRegionCountComplete = true;

foreach (Baseline baseline in corridor.Baselines)
{
    var baselineName = baseline.Name;
    if (!string.IsNullOrWhiteSpace(baselineNameFilter)
        && !baselineName.Equals(baselineNameFilter, StringComparison.OrdinalIgnoreCase))
        continue;
    baselineMatched++;

    foreach (BaselineRegion region in baseline.BaselineRegions)
    {
        regionsInMatchedBaselines++;
        var regionName = region.Name;
        if (!string.IsNullOrWhiteSpace(regionNameFilter)
            && !regionName.Equals(regionNameFilter, StringComparison.OrdinalIgnoreCase))
            continue;
        regionMatched++;

        SubassemblyTargetInfoCollection targetInfos;
        try
        {
            targetInfos = region.GetTargets();
        }
        catch (System.Exception error)
        {
            targetEnumerationErrorCount++;
            matchingRegionCountComplete = false;
            addDiagnostic("REGION_TARGETS_UNAVAILABLE", baselineName, regionName, error);
            continue;
        }

        foreach (SubassemblyTargetInfo targetInfo in targetInfos)
        {
            targetMappingTotal++;
            if (targetMappings.Count >= targetLimit) continue;

            ObjectIdCollection targetIds;
            try
            {
                targetIds = targetInfo.TargetIds;
                if (targetIds == null) throw new System.InvalidOperationException();
            }
            catch (System.Exception error)
            {
                targetEnumerationErrorCount++;
                matchingRegionCountComplete = false;
                addDiagnostic("TARGET_IDS_UNAVAILABLE", baselineName, regionName, error);
                continue;
            }
            targetIdsInReturnedMappings += targetIds.Count;
            var targetObjects = new List<object>();
            var mappingWarningCodes = new List<string>();
            var inspectedTargetIds = 0;
            foreach (ObjectId targetId in targetIds)
            {
                if (inspectedTargetIds >= targetObjectLimit) break;
                inspectedTargetIds++;
                targetIdsInspectedInReturnedMappings++;

                string targetHandle = null;
                try
                {
                    if (!targetId.IsNull) targetHandle = targetId.Handle.ToString();
                }
                catch
                {
                }

                bool? matchesOwnCorridorSurface = null;
                if (selfSurfaceCheckStatus == "complete")
                    matchesOwnCorridorSurface = !targetId.IsNull && ownSurfaceIds.Contains(targetId);
                else if (!mappingWarningCodes.Contains("SELF_SURFACE_CHECK_UNKNOWN"))
                    mappingWarningCodes.Add("SELF_SURFACE_CHECK_UNKNOWN");

                if (matchesOwnCorridorSurface == true)
                {
                    const string selfWarning = "POTENTIAL_SELF_CORRIDOR_SURFACE_TARGET";
                    mappingWarningCodes.Add(selfWarning);
                    addWarning(selfWarning, baselineName, regionName);
                }

                try
                {
                    if (targetId.IsNull)
                    {
                        targetObjectOpenErrorCount++;
                        targetObjects.Add(new { handle = (string)null, objectType = (string)null, name = (string)null, matchesOwnCorridorSurface, openErrorType = "NullObjectId" });
                        addWarning("NULL_TARGET_OBJECT_ID", baselineName, regionName);
                        continue;
                    }

                    var targetObject = Transaction.GetObject(targetId, OpenMode.ForRead);
                    var targetCivilEntity = targetObject as Autodesk.Civil.DatabaseServices.Entity;
                    targetObjects.Add(new
                    {
                        handle = targetHandle,
                        objectType = targetObject.GetType().FullName,
                        name = targetCivilEntity?.Name,
                        matchesOwnCorridorSurface,
                        openErrorType = (string)null
                    });
                }
                catch (System.Exception error)
                {
                    targetObjectOpenErrorCount++;
                    targetObjects.Add(new { handle = targetHandle, objectType = (string)null, name = (string)null, matchesOwnCorridorSurface, openErrorType = error.GetType().Name });
                    addWarning("TARGET_OBJECT_OPEN_FAILED", baselineName, regionName);
                }
            }

            targetMappings.Add(new
            {
                baselineName,
                regionName,
                startStation = region.StartStation,
                endStation = region.EndStation,
                parameterLogicalName = targetInfo.LogicalName,
                parameterDisplayName = targetInfo.DisplayName,
                targetType = targetInfo.TargetType.ToString(),
                targetToOption = targetInfo.TargetToOption.ToString(),
                subassemblyName = targetInfo.SubassemblyName,
                assemblyGroupName = targetInfo.AssemblyGroupName,
                targetIdCount = targetIds.Count,
                targetObjectsInspected = inspectedTargetIds,
                targetObjectsReturned = targetObjects.Count,
                targetObjectsTruncated = targetIds.Count > inspectedTargetIds,
                warningCodes = mappingWarningCodes,
                targets = targetObjects
            });
        }
    }
}

if (!string.IsNullOrWhiteSpace(baselineNameFilter) && baselineMatched == 0)
    addWarning("BASELINE_FILTER_NO_MATCH", baselineNameFilter, null);
else if (!string.IsNullOrWhiteSpace(regionNameFilter) && regionMatched == 0)
    addWarning("REGION_FILTER_NO_MATCH", baselineNameFilter, regionNameFilter);

return new
{
    success = true,
    corridorName = corridor.Name,
    corridorHandle = corridor.Handle.ToString(),
    baselineNameFilter = string.IsNullOrWhiteSpace(baselineNameFilter) ? null : baselineNameFilter,
    regionNameFilter = string.IsNullOrWhiteSpace(regionNameFilter) ? null : regionNameFilter,
    baselineCount = baselineTotal,
    baselineMatched,
    regionsInMatchedBaselines,
    regionMatched,
    targetMappingTotal,
    targetMappingsReturned = targetMappings.Count,
    targetMappingsTruncated = targetMappingTotal > targetMappings.Count,
    targetIdsInReturnedMappings,
    targetIdsInspectedInReturnedMappings,
    targetIdsUninspectedInReturnedMappings = targetIdsInReturnedMappings - targetIdsInspectedInReturnedMappings,
    targetObjectOpenErrorCount,
    warningObjectScope = "ObjectIds in returned mappings, limited by targetObjectLimit per mapping",
    allMatchedTargetsInspected = targetMappingTotal == targetMappings.Count
        && targetIdsInspectedInReturnedMappings == targetIdsInReturnedMappings
        && targetObjectOpenErrorCount == 0
        && targetEnumerationErrorCount == 0
        && selfSurfaceCheckStatus == "complete",
    targetLimit,
    targetObjectLimit,
    targetEnumerationErrorCount,
    targetEnumerationComplete = matchingRegionCountComplete,
    ownCorridorSurfaceCount = ownSurfaceCount,
    selfSurfaceCheckStatus,
    warningCount,
    warningCounts,
    warningsReturned = warnings.Count,
    warningsTruncated = warningCount > warnings.Count,
    warnings,
    diagnosticCount,
    diagnosticsReturned = diagnostics.Count,
    diagnosticsTruncated = diagnosticCount > diagnostics.Count,
    diagnostics,
    targetMappings
};
```

## Usage Notes
- Pass one freshly verified corridor handle; the template resolves that handle and refuses any object that is not a Corridor.
- Baseline and region filters are exact names, compared without case sensitivity. The region filter applies within matching baselines.
- The query reads target mappings from each matching `BaselineRegion.GetTargets()` result and reads mapped objects with `OpenMode.ForRead`. It does not call `SetTargets()` or `Rebuild()`.
- `TargetType`, `TargetToOption`, `LogicalName`, `DisplayName`, `SubassemblyName`, `AssemblyGroupName`, and `TargetIds` come from Civil 3D's `SubassemblyTargetInfo` API. Returned target objects are capped per mapping; totals and truncation flags remain visible.
- A `POTENTIAL_SELF_CORRIDOR_SURFACE_TARGET` warning means a target ObjectId matches a surface produced by this same corridor. Treat it as a dependency to review; the warning alone does not prove a build cycle.
- Target-specific warning counts cover only the ObjectIds in returned mappings, up to `targetObjectLimit` per mapping. `allMatchedTargetsInspected` is false if mappings or target IDs were omitted, a target could not be opened, or enumeration failed; read warning examples with that scope in mind.
- The read-only audit idea was informed by `DataShortcutCommands.cs` at [Joshua8-AI/Civil3D-mcp, pinned commit 8d1d19249b4245957330acd8af1af90d27b9c0a9](https://github.com/Joshua8-AI/Civil3D-mcp/tree/8d1d19249b4245957330acd8af1af90d27b9c0a9) and `CorridorEditingCommands.cs` at [Jjo37/new-acad, pinned commit 2085394be8dc15a885d97bc8572efc5745441290](https://github.com/Jjo37/new-acad/tree/2085394be8dc15a885d97bc8572efc5745441290); both are MIT-licensed. This skill independently uses the local Civil 3D 2025 API and ports only read-only audit concepts.
- **Proven offline and on an empty Civil 3D 2025 corridor (2026-10-01):** the exact corridor and its baseline were read successfully, zero matching regions/targets were reported, and `DBMOD` stayed 0. Populated target mappings, self-surface warnings and real dependency behavior remain **Unverified live**. An empty result does not establish the correctness of a work model.
