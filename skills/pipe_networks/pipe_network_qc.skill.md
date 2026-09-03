---
name: pipe_network_qc
category: pipe_networks
description: Check gravity pipe networks for basic connectivity and invalid-length warnings with bounded examples
requires_write: false
parameters:
  - name: networkName
    type: string
    required: false
    description: Optional exact network name filter
  - name: limit
    type: int
    required: false
    description: Maximum networks to return (default 25)
  - name: partLimit
    type: int
    required: false
    description: Maximum pipes and maximum structures inspected per returned network (default 1000 each)
  - name: issueLimit
    type: int
    required: false
    description: Maximum issue examples returned per network (default 25)
---

## Code Template

```csharp
var networkNameFilter = "";
var limit = 25;
var partLimit = 1000;
var issueLimit = 25;
if (limit < 1 || partLimit < 1 || issueLimit < 1)
    return new { error = "limits must be at least 1" };

var networks = new List<object>();
var total = 0;

foreach (ObjectId networkId in CivilDoc.GetPipeNetworkIds())
{
    var network = Transaction.GetObject(networkId, OpenMode.ForRead) as Network;
    if (network == null) continue;
    if (!string.IsNullOrWhiteSpace(networkNameFilter)
        && !network.Name.Equals(networkNameFilter, StringComparison.OrdinalIgnoreCase))
        continue;

    total++;
    if (networks.Count >= limit) continue;

    var pipeIds = network.GetPipeIds();
    var structureIds = network.GetStructureIds();
    var issues = new List<object>();
    var issueCount = 0;
    Action<string, string, string, string> addIssue = (code, objectType, name, handle) => {
        issueCount++;
        if (issues.Count < issueLimit)
            issues.Add(new { code, objectType, name, handle });
    };

    var pipesInspected = 0;
    foreach (ObjectId pipeId in pipeIds)
    {
        if (pipesInspected >= partLimit) break;
        var pipe = Transaction.GetObject(pipeId, OpenMode.ForRead) as Pipe;
        if (pipe == null) continue;
        pipesInspected++;

        if (pipe.StartStructureId.IsNull)
            addIssue("PIPE_START_UNCONNECTED", "pipe", pipe.Name, pipe.Handle.ToString());
        if (pipe.EndStructureId.IsNull)
            addIssue("PIPE_END_UNCONNECTED", "pipe", pipe.Name, pipe.Handle.ToString());
        if (double.IsNaN(pipe.Length3D) || double.IsInfinity(pipe.Length3D) || pipe.Length3D <= 0)
            addIssue("PIPE_LENGTH_INVALID", "pipe", pipe.Name, pipe.Handle.ToString());
    }

    var structuresInspected = 0;
    foreach (ObjectId structureId in structureIds)
    {
        if (structuresInspected >= partLimit) break;
        var structure = Transaction.GetObject(structureId, OpenMode.ForRead) as Structure;
        if (structure == null) continue;
        structuresInspected++;

        if (structure.ConnectedPipesCount == 0)
            addIssue("STRUCTURE_DISCONNECTED", "structure", structure.Name, structure.Handle.ToString());
    }

    networks.Add(new {
        name = network.Name,
        handle = network.Handle.ToString(),
        partsList = network.PartsListId.IsNull ? null : network.PartsListName,
        referenceSurface = network.ReferenceSurfaceId.IsNull ? null : network.ReferenceSurfaceName,
        referenceAlignment = network.ReferenceAlignmentId.IsNull ? null : network.ReferenceAlignmentName,
        isReference = network.IsReferenceObject,
        isReferenceStale = network.IsReferenceStale,
        pipeCount = pipeIds.Count,
        pipesInspected,
        structureCount = structureIds.Count,
        structuresInspected,
        inspectionTruncated = pipeIds.Count > pipesInspected || structureIds.Count > structuresInspected,
        issueCountInInspectedParts = issueCount,
        issuesReturned = issues.Count,
        issuesTruncated = issueCount > issues.Count,
        issues
    });
}

return new {
    total,
    returned = networks.Count,
    truncated = total > networks.Count,
    limit,
    partLimit,
    issueLimit,
    networkNameFilter = string.IsNullOrWhiteSpace(networkNameFilter) ? null : networkNameFilter,
    networks
};
```

## Usage Notes
- Warnings identify basic connectivity or invalid-length conditions; they do not replace hydraulic or design checks
- An unconnected pipe end can be intentional, so review warnings before any correction
- `inspectionTruncated` means the QC counts cover only the inspected subset
