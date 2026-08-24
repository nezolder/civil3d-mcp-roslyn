---
name: list_alignments
category: alignments
description: List all alignments in the drawing with name, length, start/end stations
requires_write: false
parameters: []
---

## Code Template

```csharp
var alignments = new List<object>();

foreach (ObjectId id in CivilDoc.GetAlignmentIds())
{
    var a = Transaction.GetObject(id, OpenMode.ForRead) as Alignment;
    if (a == null) continue;

    alignments.Add(new {
        name = a.Name,
        handle = a.Handle.ToString(),
        length = a.Length,
        startStation = a.StartingStation,
        endStation = a.EndingStation,
        layer = a.Layer,
        style = a.StyleName,
        entityCount = a.Entities.Count
    });
}

return new { count = alignments.Count, alignments };
```

## Usage Notes
- Returns all alignments regardless of site
- Length is in drawing units (meters or feet)
- Use station_offset skill for coordinate conversions
