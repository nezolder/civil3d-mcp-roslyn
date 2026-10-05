---
name: data_reference_audit
category: references
description: Audit bounded data-reference state for selected Civil 3D object categories in the active drawing
requires_write: false
aliases: ["Civil 3D adatkapcsolatok DREF állapotának auditja", "data reference audit by selected object category", "DREF objektumok állapotvizsgálata rajzban"]
workflow_tags: ["audit"]
tested_civil_version: "2025"
validation_summary: "Offline + live: local objects and incomplete-scan limits, DBMOD unchanged. Real invalid/stale DREF source states unverified."
parameters:
  - name: categoryFilter
    type: string
    required: false
    description: all or one supported category (default all)
  - name: onlyReferences
    type: bool
    required: false
    description: Return reference and unknown-state details only (default true)
  - name: limit
    type: int
    required: false
    description: Maximum detail rows returned per category (default 25, 1-100)
  - name: scanLimit
    type: int
    required: false
    description: Maximum objects whose state is inspected per category (default 5000, 1-10000)
---

## Code Template

```csharp
string categoryFilter = "all";
bool onlyReferences = true;
int limit = 25;
int scanLimit = 5000;

if (categoryFilter == null || categoryFilter.Trim().Length == 0 || categoryFilter.Trim().Length > 32)
    return new { success = false, error = "categoryFilter must be all or one supported category name." };
categoryFilter = categoryFilter.Trim();
if (limit < 1 || limit > 100 || scanLimit < 1 || scanLimit > 10000)
    return new { success = false, error = "limit must be 1-100 and scanLimit must be 1-10000." };
var validCategory = categoryFilter.Equals("all", StringComparison.OrdinalIgnoreCase)
    || categoryFilter.Equals("alignments", StringComparison.OrdinalIgnoreCase)
    || categoryFilter.Equals("profiles", StringComparison.OrdinalIgnoreCase)
    || categoryFilter.Equals("sampleLineGroups", StringComparison.OrdinalIgnoreCase)
    || categoryFilter.Equals("featureLines", StringComparison.OrdinalIgnoreCase)
    || categoryFilter.Equals("corridors", StringComparison.OrdinalIgnoreCase)
    || categoryFilter.Equals("surfaces", StringComparison.OrdinalIgnoreCase)
    || categoryFilter.Equals("pipeNetworks", StringComparison.OrdinalIgnoreCase);
if (!validCategory)
    return new { success = false, error = "categoryFilter must be all or one declared category name.", categoryFilter };

// Host access starts here.
var allCategories = new[] { "alignments", "profiles", "sampleLineGroups", "featureLines", "corridors", "surfaces", "pipeNetworks" };
var selectedCategories = categoryFilter.Equals("all", StringComparison.OrdinalIgnoreCase)
    ? allCategories
    : allCategories.Where(name => name.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase)).ToArray();

var totals = new Dictionary<string, int>(StringComparer.Ordinal);
var stateScanned = new Dictionary<string, int>(StringComparer.Ordinal);
var referenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
var invalidCounts = new Dictionary<string, int>(StringComparer.Ordinal);
var staleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
var missingSourceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
var unknownStateCounts = new Dictionary<string, int>(StringComparer.Ordinal);
var problemCounts = new Dictionary<string, int>(StringComparer.Ordinal);
var objectReadErrors = new Dictionary<string, int>(StringComparer.Ordinal);
var enumerationErrorsByCategory = new Dictionary<string, int>(StringComparer.Ordinal);
var stateApiErrors = new Dictionary<string, int>(StringComparer.Ordinal);
var detailMatchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
var rowsByCategory = new Dictionary<string, List<object>>(StringComparer.Ordinal);

foreach (var category in selectedCategories)
{
    totals[category] = 0;
    stateScanned[category] = 0;
    referenceCounts[category] = 0;
    invalidCounts[category] = 0;
    staleCounts[category] = 0;
    missingSourceCounts[category] = 0;
    unknownStateCounts[category] = 0;
    problemCounts[category] = 0;
    objectReadErrors[category] = 0;
    enumerationErrorsByCategory[category] = 0;
    stateApiErrors[category] = 0;
    detailMatchCounts[category] = 0;
    rowsByCategory[category] = new List<object>();
}

var diagnostics = new List<object>();
var diagnosticCount = 0;
var enumerationErrorCount = 0;

Action<string, string, string, string> addDiagnostic = (category, phase, parentHandle, errorType) =>
{
    diagnosticCount++;
    if (diagnostics.Count < 50)
        diagnostics.Add(new { category, phase, parentHandle, errorType });
};

Action<string, string, System.Exception> addEnumerationError = (category, parentHandle, error) =>
{
    enumerationErrorCount++;
    enumerationErrorsByCategory[category]++;
    addDiagnostic(category, "enumeration", parentHandle, error?.GetType().Name);
};

Action<string, string, ObjectId> inspectCandidate = (category, parentName, id) =>
{
    totals[category]++;
    if (stateScanned[category] >= scanLimit) return;
    stateScanned[category]++;

    string handle = null;
    try
    {
        if (!id.IsNull) handle = id.Handle.ToString();
    }
    catch
    {
    }

    Autodesk.Civil.DatabaseServices.Entity entity;
    try
    {
        entity = Transaction.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Entity;
        if (entity == null) throw new System.InvalidOperationException();
    }
    catch (System.Exception error)
    {
        objectReadErrors[category]++;
        unknownStateCounts[category]++;
        problemCounts[category]++;
        detailMatchCounts[category]++;
        addDiagnostic(category, "objectOpen", handle, error.GetType().Name);
        var failedRows = rowsByCategory[category];
        if (failedRows.Count < limit)
        {
            failedRows.Add(new
            {
                parentName,
                handle,
                objectType = (string)null,
                name = (string)null,
                objectReadErrorType = error.GetType().Name,
                isReference = (bool?)null,
                isValid = (bool?)null,
                isStale = (bool?)null,
                sourceExisting = (bool?)null,
                sourceIdentityStatus = "unknown",
                sourceIdentity = (object)null,
                stateReadErrors = new string[0]
            });
        }
        return;
    }

    var stateReadErrors = new List<string>();
    Func<string, Func<bool>, bool?> readFlag = (flagName, getter) =>
    {
        try { return getter(); }
        catch (System.Exception error)
        {
            stateReadErrors.Add(flagName + ":" + error.GetType().Name);
            return null;
        }
    };

    var isReference = readFlag("isReference", () => entity.IsReferenceObject);
    bool? isValid = null;
    bool? isStale = null;
    bool? sourceExisting = null;
    object sourceIdentity = null;
    var sourceIdentityStatus = "notReference";

    if (isReference == true)
    {
        referenceCounts[category]++;
        isValid = readFlag("isValid", () => entity.IsReferenceValid);
        isStale = readFlag("isStale", () => entity.IsReferenceStale);
        sourceExisting = readFlag("sourceExisting", () => entity.IsReferencedSourceExisting);

        var isCwsReference = readFlag("isCwsReference", () => entity.IsCWSReferenceObject);
        if (isCwsReference == true)
        {
            sourceIdentityStatus = "notDataShortcut";
        }
        else if (isCwsReference == false)
        {
            try
            {
                var referenceKey = entity.GetReferenceInfo();
                if (referenceKey == null)
                {
                    sourceIdentityStatus = "unavailable";
                }
                else
                {
                    var sourceDrawingExists = readFlag("sourceDrawingExists", () => referenceKey.IsSourceDrawingExistent);
                    sourceIdentity = new
                    {
                        sourceName = referenceKey.Name,
                        sourceType = referenceKey.Type.ToString(),
                        sourceDrawingExists
                    };
                    sourceIdentityStatus = "available";
                }
            }
            catch (System.Exception error)
            {
                stateReadErrors.Add("sourceIdentity:" + error.GetType().Name);
                sourceIdentityStatus = "unknown";
            }
        }
        else
        {
            sourceIdentityStatus = "unknown";
        }
    }
    else if (!isReference.HasValue)
    {
        sourceIdentityStatus = "unknown";
    }

    var coreStateUnknown = !isReference.HasValue
        || (isReference == true && (!isValid.HasValue || !isStale.HasValue || !sourceExisting.HasValue));
    if (coreStateUnknown)
        unknownStateCounts[category]++;

    var invalid = isReference == true && isValid == false;
    var stale = isReference == true && isStale == true;
    var sourceMissing = isReference == true && sourceExisting == false;
    if (invalid) invalidCounts[category]++;
    if (stale) staleCounts[category]++;
    if (sourceMissing) missingSourceCounts[category]++;
    if (invalid || stale || sourceMissing || coreStateUnknown)
        problemCounts[category]++;

    stateApiErrors[category] += stateReadErrors.Count;
    foreach (var stateError in stateReadErrors)
    {
        var separator = stateError.IndexOf(':');
        var errorType = separator >= 0 ? stateError.Substring(separator + 1) : "Unknown";
        addDiagnostic(category, "stateRead:" + (separator >= 0 ? stateError.Substring(0, separator) : stateError), handle, errorType);
    }

    string entityName = null;
    try { entityName = entity.Name; }
    catch (System.Exception error)
    {
        stateReadErrors.Add("name:" + error.GetType().Name);
        stateApiErrors[category]++;
        addDiagnostic(category, "name", handle, error.GetType().Name);
    }

    var isDetailMatch = !onlyReferences || isReference == true || !isReference.HasValue;
    if (isDetailMatch)
    {
        detailMatchCounts[category]++;
        var rows = rowsByCategory[category];
        if (rows.Count < limit)
        {
            rows.Add(new
            {
                parentName,
                handle = entity.Handle.ToString(),
                objectType = entity.GetType().FullName,
                name = entityName,
                objectReadErrorType = (string)null,
                isReference,
                isValid,
                isStale,
                sourceExisting,
                sourceIdentityStatus,
                sourceIdentity,
                stateReadErrors
            });
        }
    }
};

var needAlignments = selectedCategories.Contains("alignments")
    || selectedCategories.Contains("profiles")
    || selectedCategories.Contains("sampleLineGroups");
ObjectIdCollection alignmentIds = null;
if (needAlignments)
{
    try
    {
        alignmentIds = CivilDoc.GetAlignmentIds();
        if (selectedCategories.Contains("alignments"))
        {
            foreach (ObjectId alignmentId in alignmentIds)
                inspectCandidate("alignments", null, alignmentId);
        }
    }
    catch (System.Exception error)
    {
        if (selectedCategories.Contains("alignments"))
            addEnumerationError("alignments", null, error);
        if (selectedCategories.Contains("profiles"))
            addEnumerationError("profiles", null, error);
        if (selectedCategories.Contains("sampleLineGroups"))
            addEnumerationError("sampleLineGroups", null, error);
        alignmentIds = null;
    }

    if (alignmentIds != null
        && (selectedCategories.Contains("profiles") || selectedCategories.Contains("sampleLineGroups")))
    {
        foreach (ObjectId alignmentId in alignmentIds)
        {
            Alignment alignment;
            string parentHandle = null;
            try
            {
                parentHandle = alignmentId.Handle.ToString();
                alignment = Transaction.GetObject(alignmentId, OpenMode.ForRead) as Alignment;
                if (alignment == null) throw new System.InvalidOperationException();
            }
            catch (System.Exception error)
            {
                if (selectedCategories.Contains("profiles"))
                    addEnumerationError("profiles", parentHandle, error);
                if (selectedCategories.Contains("sampleLineGroups"))
                    addEnumerationError("sampleLineGroups", parentHandle, error);
                continue;
            }

            if (selectedCategories.Contains("profiles"))
            {
                try
                {
                    foreach (ObjectId profileId in alignment.GetProfileIds())
                        inspectCandidate("profiles", alignment.Name, profileId);
                }
                catch (System.Exception error)
                {
                    addEnumerationError("profiles", parentHandle, error);
                }
            }

            if (selectedCategories.Contains("sampleLineGroups"))
            {
                try
                {
                    foreach (ObjectId groupId in alignment.GetSampleLineGroupIds())
                        inspectCandidate("sampleLineGroups", alignment.Name, groupId);
                }
                catch (System.Exception error)
                {
                    addEnumerationError("sampleLineGroups", parentHandle, error);
                }
            }
        }
    }
}

if (selectedCategories.Contains("featureLines"))
{
    try
    {
        foreach (ObjectId siteId in CivilDoc.GetSiteIds())
        {
            Site site;
            string parentHandle = null;
            try
            {
                parentHandle = siteId.Handle.ToString();
                site = Transaction.GetObject(siteId, OpenMode.ForRead) as Site;
                if (site == null) throw new System.InvalidOperationException();
            }
            catch (System.Exception error)
            {
                addEnumerationError("featureLines", parentHandle, error);
                continue;
            }

            try
            {
                foreach (ObjectId featureLineId in site.GetFeatureLineIds())
                    inspectCandidate("featureLines", site.Name, featureLineId);
            }
            catch (System.Exception error)
            {
                addEnumerationError("featureLines", parentHandle, error);
            }
        }
    }
    catch (System.Exception error)
    {
        addEnumerationError("featureLines", null, error);
    }
}

if (selectedCategories.Contains("corridors"))
{
    try
    {
        foreach (ObjectId corridorId in CivilDoc.CorridorCollection)
            inspectCandidate("corridors", null, corridorId);
    }
    catch (System.Exception error)
    {
        addEnumerationError("corridors", null, error);
    }
}

if (selectedCategories.Contains("surfaces"))
{
    try
    {
        foreach (ObjectId surfaceId in CivilDoc.GetSurfaceIds())
            inspectCandidate("surfaces", null, surfaceId);
    }
    catch (System.Exception error)
    {
        addEnumerationError("surfaces", null, error);
    }
}

if (selectedCategories.Contains("pipeNetworks"))
{
    try
    {
        foreach (ObjectId networkId in CivilDoc.GetPipeNetworkIds())
            inspectCandidate("pipeNetworks", null, networkId);
    }
    catch (System.Exception error)
    {
        addEnumerationError("pipeNetworks", null, error);
    }
}

var categoryResults = new List<object>();
foreach (var category in selectedCategories)
{
    var total = totals[category];
    var scanned = stateScanned[category];
    var enumerationComplete = enumerationErrorsByCategory[category] == 0;
    int? stateUnscannedCount = enumerationComplete ? Math.Max(total - scanned, 0) : (int?)null;
    var stateScanComplete = enumerationComplete
        && stateUnscannedCount == 0
        && objectReadErrors[category] == 0
        && unknownStateCounts[category] == 0;
    var records = rowsByCategory[category];
    categoryResults.Add(new
    {
        category,
        objectTotal = total,
        stateScanned = scanned,
        stateUnscannedCount,
        stateScanCapped = !enumerationComplete || scanned < total,
        stateScanComplete,
        enumerationComplete,
        enumerationErrorCount = enumerationErrorsByCategory[category],
        objectReadErrorCount = objectReadErrors[category],
        stateApiErrorCount = stateApiErrors[category],
        referenceCountScanned = referenceCounts[category],
        invalidReferenceCountScanned = invalidCounts[category],
        staleReferenceCountScanned = staleCounts[category],
        sourceMissingCountScanned = missingSourceCounts[category],
        unknownReferenceStateCountScanned = unknownStateCounts[category],
        problemCountScanned = problemCounts[category],
        detailMatchCountScanned = detailMatchCounts[category],
        detailRecordsReturned = records.Count,
        detailRecordsTruncated = detailMatchCounts[category] > records.Count,
        records
    });
}

return new
{
    success = true,
    categoryFilter,
    onlyReferences,
    detailLimitPerCategory = limit,
    stateScanLimitPerCategory = scanLimit,
    categoryCount = selectedCategories.Length,
    enumerationErrorCount,
    diagnosticsReturned = diagnostics.Count,
    diagnosticsTruncated = diagnosticCount > diagnostics.Count,
    diagnostics,
    categories = categoryResults
};
```

## Usage Notes
- The bounded inventory covers alignments, profiles, sample-line groups, site feature lines, corridors, surfaces, and gravity pipe networks. Use categoryFilter to scan one category and avoid traversing unrelated Civil collections where possible. These declared categories are not every Civil 3D object type.
- The query enumerates object IDs to count each selected category but opens at most scanLimit objects per category to read reference state. referenceCountScanned, issue counts, and unknownReferenceStateCountScanned describe only that inspected scope. stateScanComplete is false when IDs were unscanned, enumeration failed, an object could not be opened, or a required reference-state value is unknown. An incomplete scan must not be treated as a complete DREF audit.
- onlyReferences=true filters returned details, not the state scan or counts. Known local objects are scanned but omitted from detail rows. An object whose reference state is unknown is included as a possible reference. limit caps detail rows only; detailRecordsTruncated reports additional matching scanned records.
- isReference, isValid, isStale, and sourceExisting are null when a reference-state API call fails or the value does not apply. sourceExisting is Civil 3D's Entity.IsReferencedSourceExisting result: it reports whether the source entity was available when the reference opened, not whether every source file can be reached now.
- When a non-worksharing reference supports GetReferenceInfo(), the query reports source name, source type, and IsSourceDrawingExistent. It never returns the source path or traverses source drawings or folders. A failing or unsupported source-identification call leaves sourceIdentityStatus unknown.
- The read-only inventory idea was informed by DataShortcutCommands.cs at [Joshua8-AI/Civil3D-mcp, pinned commit 8d1d19249b4245957330acd8af1af90d27b9c0a9](https://github.com/Joshua8-AI/Civil3D-mcp/tree/8d1d19249b4245957330acd8af1af90d27b9c0a9) and CorridorEditingCommands.cs at [Jjo37/new-acad, pinned commit 2085394be8dc15a885d97bc8572efc5745441290](https://github.com/Jjo37/new-acad/tree/2085394be8dc15a885d97bc8572efc5745441290); both are MIT-licensed. This skill independently uses locally verified Civil 3D 2025 members and ports only read-only audit concepts.
- **Proven offline and on local Civil 3D 2025 objects (2026-10-01):** category selection, detail caps and an incomplete state scan were exercised without changing `DBMOD`. Scanning one of two local surfaces reported one unscanned object and `stateScanComplete=false`. Actual DREF source identification, invalid/stale references and worksharing cases remain **Unverified live**.
