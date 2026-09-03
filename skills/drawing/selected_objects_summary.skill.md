---
name: selected_objects_summary
category: drawing
description: Summarize the currently preselected objects by type and layer with a bounded sample
requires_write: false
parameters:
  - name: limit
    type: int
    required: false
    description: Maximum selected objects to inspect (default 200)
---

## Code Template

```csharp
var limit = 200;
if (limit < 1) return new { error = "limit must be at least 1" };

var selection = Editor.SelectImplied();
var ids = selection.Status == PromptStatus.OK && selection.Value != null
    ? selection.Value.GetObjectIds()
    : Array.Empty<ObjectId>();
var returned = Math.Min(ids.Length, limit);
var sampleLimit = Math.Min(20, limit);
var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var byLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var sample = new List<object>();

for (var index = 0; index < returned; index++)
{
    var dbObject = Transaction.GetObject(ids[index], OpenMode.ForRead);
    var entity = dbObject as Autodesk.AutoCAD.DatabaseServices.Entity;
    var typeName = dbObject.GetType().FullName ?? dbObject.GetType().Name;
    var layerName = entity == null ? "<not an entity>" : entity.Layer;

    byType[typeName] = byType.TryGetValue(typeName, out var typeCount) ? typeCount + 1 : 1;
    byLayer[layerName] = byLayer.TryGetValue(layerName, out var layerCount) ? layerCount + 1 : 1;

    if (sample.Count < sampleLimit)
    {
        sample.Add(new {
            handle = dbObject.Handle.ToString(),
            type = typeName,
            layer = layerName
        });
    }
}

return new {
    total = ids.Length,
    returned,
    truncated = ids.Length > returned,
    limit,
    byType = byType
        .OrderByDescending(item => item.Value)
        .ThenBy(item => item.Key)
        .Select(item => new { type = item.Key, count = item.Value })
        .ToList(),
    byLayer = byLayer
        .OrderByDescending(item => item.Value)
        .ThenBy(item => item.Key)
        .Select(item => new { layer = item.Key, count = item.Value })
        .ToList(),
    sample
};
```

## Usage Notes
- Uses only the current implied (preselected) selection; it does not prompt or modify the drawing
- `total` is the selection size and `returned` is the number inspected
- Type and layer counts cover only the inspected objects when `truncated` is true
