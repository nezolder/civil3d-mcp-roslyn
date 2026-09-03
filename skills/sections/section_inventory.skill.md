---
name: section_inventory
category: sections
description: Inventory sample-line groups and return a bounded list of sample lines with section and view counts
requires_write: false
parameters:
  - name: alignmentName
    type: string
    required: false
    description: Optional exact alignment name filter
  - name: limit
    type: int
    required: false
    description: Maximum sample-line records to return (default 200)
---

## Code Template

```csharp
var alignmentNameFilter = "";
var limit = 200;
var groupLimit = 50;
if (limit < 1) return new { error = "limit must be at least 1" };

var groups = new List<object>();
var sampleLines = new List<object>();
var groupTotal = 0;
var total = 0;

foreach (ObjectId alignmentId in CivilDoc.GetAlignmentIds())
{
    var alignment = Transaction.GetObject(alignmentId, OpenMode.ForRead) as Alignment;
    if (alignment == null) continue;
    if (!string.IsNullOrWhiteSpace(alignmentNameFilter)
        && !alignment.Name.Equals(alignmentNameFilter, StringComparison.OrdinalIgnoreCase))
        continue;

    foreach (ObjectId groupId in alignment.GetSampleLineGroupIds())
    {
        var group = Transaction.GetObject(groupId, OpenMode.ForRead) as SampleLineGroup;
        if (group == null) continue;

        groupTotal++;
        var sampleLineIds = group.GetSampleLineIds();
        total += sampleLineIds.Count;

        if (groups.Count < groupLimit)
        {
            groups.Add(new {
                alignmentName = alignment.Name,
                name = group.Name,
                handle = group.Handle.ToString(),
                sampleLineCount = sampleLineIds.Count,
                sectionViewGroupCount = group.SectionViewGroups.Count
            });
        }

        foreach (ObjectId sampleLineId in sampleLineIds)
        {
            if (sampleLines.Count >= limit) continue;
            var sampleLine = Transaction.GetObject(sampleLineId, OpenMode.ForRead) as SampleLine;
            if (sampleLine == null) continue;

            sampleLines.Add(new {
                alignmentName = alignment.Name,
                groupName = group.Name,
                name = sampleLine.Name,
                handle = sampleLine.Handle.ToString(),
                station = sampleLine.Station,
                style = sampleLine.StyleName,
                sectionCount = sampleLine.GetSectionIds().Count,
                sectionViewCount = sampleLine.GetSectionViewIds().Count
            });
        }
    }
}

return new {
    total,
    returned = sampleLines.Count,
    truncated = total > sampleLines.Count,
    limit,
    groupTotal,
    groupsReturned = groups.Count,
    groupsTruncated = groupTotal > groups.Count,
    groupLimit,
    alignmentNameFilter = string.IsNullOrWhiteSpace(alignmentNameFilter) ? null : alignmentNameFilter,
    groups,
    sampleLines
};
```

## Usage Notes
- `total` is the exact sample-line count for the selected alignment scope
- Section and section-view counts are returned only for the bounded `sampleLines` list
- The skill does not create or update sample lines or section views
