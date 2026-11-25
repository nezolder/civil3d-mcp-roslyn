---
name: assembly_inventory
category: corridors
description: List assemblies with their groups and subassemblies (class or Composer package, side, attachment, input parameters by key) and the corridor regions that use each one
requires_write: false
aliases: ["szerelvények és alszerelvények listája paraméterekkel", "assembly and subassembly inventory with parameters", "melyik régió melyik szerelvényt használja", "assembly usage by corridor region"]
workflow_tags: ["source_state", "audit"]
tested_civil_version: "2025"
validation_summary: "Offline: compiles against the Civil 3D 2025 API. Not yet run in Civil 3D."
parameters:
  - name: assemblyName
    type: string
    required: false
    description: Exact assembly name; empty lists every assembly (default "")
  - name: includeParameters
    type: bool
    required: false
    description: List each subassembly's input parameters by key with their default values (default true)
  - name: parameterLimit
    type: int
    required: false
    description: Maximum parameters listed per subassembly, 1 to 200 (default 60)
  - name: subassemblyLimit
    type: int
    required: false
    description: Maximum subassemblies listed per assembly, 1 to 200 (default 60)
  - name: limit
    type: int
    required: false
    description: Maximum assemblies listed, 1 to 200 (default 50)
---

## Code Template

```csharp
var assemblyName = "";
var includeParameters = true;
var parameterLimit = 60;
var subassemblyLimit = 60;
var limit = 50;

if (parameterLimit < 1 || parameterLimit > 200) return new { success = false, error = "parameterLimit must be between 1 and 200." };
if (subassemblyLimit < 1 || subassemblyLimit > 200) return new { success = false, error = "subassemblyLimit must be between 1 and 200." };
if (limit < 1 || limit > 200) return new { success = false, error = "limit must be between 1 and 200." };

// Which corridor regions use which assembly.
var usage = new Dictionary<ObjectId, List<object>>();
var regionCount = 0;
foreach (ObjectId corridorId in CivilDoc.CorridorCollection)
{
    var corridor = Transaction.GetObject(corridorId, OpenMode.ForRead) as Corridor;
    if (corridor == null) continue;
    foreach (Baseline baseline in corridor.Baselines)
    {
        foreach (BaselineRegion region in baseline.BaselineRegions)
        {
            regionCount++;
            if (region.AssemblyId.IsNull) continue;
            if (!usage.TryGetValue(region.AssemblyId, out var rows)) usage[region.AssemblyId] = rows = new List<object>();
            if (rows.Count < 100)
                rows.Add(new
                {
                    corridor = corridor.Name,
                    corridorHandle = corridor.Handle.ToString(),
                    baseline = baseline.Name,
                    region = region.Name,
                    startStation = Math.Round(region.StartStation, 4),
                    endStation = Math.Round(region.EndStation, 4)
                });
        }
    }
}

var subassemblyNames = new Dictionary<ObjectId, string>();
string NameOf(ObjectId id)
{
    if (id.IsNull) return null;
    if (subassemblyNames.TryGetValue(id, out var known)) return known;
    string name;
    try { name = (Transaction.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Subassembly)?.Name; }
    catch (System.Exception) { name = null; }
    subassemblyNames[id] = name;
    return name;
}

object ParametersOf(Autodesk.Civil.DatabaseServices.Subassembly sub)
{
    var rows = new List<(int order, string key, string displayName, string type, object value)>();
    foreach (var p in sub.ParamsDouble) rows.Add((p.DisplayOrder, p.Key, p.DisplayName, "double", p.Value));
    foreach (var p in sub.ParamsLong) rows.Add((p.DisplayOrder, p.Key, p.DisplayName, "long", p.Value));
    foreach (var p in sub.ParamsBool) rows.Add((p.DisplayOrder, p.Key, p.DisplayName, "bool", p.Value));
    foreach (var p in sub.ParamsString) rows.Add((p.DisplayOrder, p.Key, p.DisplayName, "string", p.Value));
    // A flat list keeps the reply within the result depth limit of 8.
    return rows.OrderBy(r => r.order).Take(parameterLimit)
        .Select(r => new { key = r.key, displayName = r.displayName, type = r.type, value = r.value })
        .ToList();
}
int ParameterCount(Autodesk.Civil.DatabaseServices.Subassembly sub)
    => sub.ParamsDouble.Count + sub.ParamsLong.Count + sub.ParamsBool.Count + sub.ParamsString.Count;

var assemblies = new List<object>();
var total = 0;
foreach (ObjectId assemblyId in CivilDoc.AssemblyCollection)
{
    var assembly = Transaction.GetObject(assemblyId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Assembly;
    if (assembly == null) continue;
    if (!string.IsNullOrWhiteSpace(assemblyName)
        && !string.Equals(assembly.Name, assemblyName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
    total++;
    if (assemblies.Count >= limit) continue;

    var groupNames = new List<string>();
    var subs = new List<object>();
    var subassemblyTotal = 0;
    var listedSubassemblies = 0;
    foreach (AssemblyGroup group in assembly.Groups)
    {
        groupNames.Add(group.Name);
        foreach (ObjectId subId in group.GetSubassemblyIds())
        {
            subassemblyTotal++;
            if (listedSubassemblies >= subassemblyLimit) continue;
            var sub = Transaction.GetObject(subId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Subassembly;
            if (sub == null) continue;
            listedSubassemblies++;
            var generator = sub.GeometryGenerator;
            subs.Add(new
            {
                group = group.Name,
                name = sub.Name,
                handle = sub.Handle.ToString(),
                macroOrClassName = generator?.MacroOrClassName,
                projectOrAssemblyName = generator?.ProjectOrAssemblyName,
                generateMode = generator?.GeometryGenerateMode.ToString(),
                fromSubassemblyComposer = sub.IsFromSubassemblyComposer,
                side = sub.HasSide ? sub.Side.ToString() : null,
                hookedTo = NameOf(sub.SubassemblyHookTo),
                hookPointIndex = sub.SubassemblyHookTo.IsNull ? (int?)null : sub.PointIndexHookTo,
                offsetToAssembly = new { offset = Math.Round(sub.OffsetToAssembly.X, 4), elevation = Math.Round(sub.OffsetToAssembly.Y, 4) },
                offsetAssembly = sub.AttachedToOffsetAssembly ? sub.OffsetAssemblyName : null,
                status = sub.Status.ToString(),
                layer = sub.Layer,
                parameterTotal = ParameterCount(sub),
                parameters = includeParameters ? ParametersOf(sub) : null
            });
        }
    }

    usage.TryGetValue(assemblyId, out var usedBy);
    assemblies.Add(new
    {
        name = assembly.Name,
        handle = assembly.Handle.ToString(),
        type = assembly.Type.ToString(),
        style = assembly.StyleName,
        codeSetStyle = assembly.CodeSetStyleName,
        layer = assembly.Layer,
        location = new { x = Math.Round(assembly.Location.X, 4), y = Math.Round(assembly.Location.Y, 4) },
        offsetAssemblies = assembly.OffsetAssemblies.Count,
        groups = groupNames,
        subassemblyTotal,
        subassembliesTruncated = subassemblyTotal > listedSubassemblies,
        subassemblies = subs,
        usedByRegions = usedBy?.Count ?? 0,
        usedBy = usedBy ?? new List<object>()
    });
}

return new
{
    success = true,
    total,
    returned = assemblies.Count,
    truncated = total > assemblies.Count,
    assemblyNameFilter = string.IsNullOrWhiteSpace(assemblyName) ? null : assemblyName.Trim(),
    corridorRegionsInDrawing = regionCount,
    assemblies
};
```

## Usage Notes

- Read-only. It reads the assemblies as they are in the drawing; nothing is created, rebuilt or opened for write.
- Subassemblies are listed in one list per assembly, each with its `group`. `parameters` are the subassembly's own input values (`key`, `displayName`, `type`, `value`), sorted as the Properties dialog shows them; `parameterTotal` counts all of them. They are the assembly's defaults, not the values a corridor applies at a station: targets and station overrides can change those. Find a parameter by `key`; `displayName` follows the language of the installation.
- The values applied at a station (`AppliedSubassembly.Parameters`) are deliberately not read here: reading them where a parameter is overridden for a station range has crashed Civil 3D in field use. Use `corridor_applied_section` for the built geometry at a station instead.
- `macroOrClassName` names the stock subassembly class (for example `Subassembly.BasicLane`) or, with `fromSubassemblyComposer: true`, the Subassembly Composer package. `hookedTo` and `hookPointIndex` say where a subassembly is attached; `null` means it hangs on the assembly's baseline point.
- `side` is `null` for a subassembly without a side setting. `offsetToAssembly` is its origin relative to the assembly's baseline point (offset, elevation).
- `usedBy` lists the corridor regions that apply the assembly (first 100); an assembly with `usedByRegions: 0` is not used by any corridor in this drawing. Assemblies in data-referenced or xref'd drawings are not seen.
- Units are the drawing's units.
