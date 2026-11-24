// Live smoke test fixture: an assembly with the stock BasicLane on both sides
// and a corridor along the fixture alignment and design profile, rebuilt once.
// When the stock subassembly cannot be loaded the corridor is still made with
// an empty assembly, and the reply says so.
var alignmentHandle = "ALIGNMENT_HANDLE"; // smoke-param
var profileHandle = "PROFILE_HANDLE"; // smoke-param
var assemblyName = "ASSEMBLY_NAME"; // smoke-param
var corridorName = "CORRIDOR_NAME"; // smoke-param
var assemblyX = 0.0; // smoke-param
var assemblyY = 0.0; // smoke-param

Func<string, ObjectId> resolve = handle =>
    Database.GetObjectId(false, new Handle(System.Convert.ToInt64(handle, 16)), 0);
var alignmentId = resolve(alignmentHandle);
var profileId = resolve(profileHandle);
if (!(Transaction.GetObject(alignmentId, OpenMode.ForRead) is Alignment))
    throw new InvalidOperationException("The fixture alignment was not found.");
if (!(Transaction.GetObject(profileId, OpenMode.ForRead) is Profile))
    throw new InvalidOperationException("The fixture design profile was not found.");

var location = new Point3d(assemblyX, assemblyY, 0);
var assemblyId = CivilDoc.AssemblyCollection.Add(assemblyName, AssemblyType.UndividedCrownedRoad, location);
var assembly = (Assembly)Transaction.GetObject(assemblyId, OpenMode.ForWrite);

var subassemblies = new List<string>();
string subassemblyWarning = null;
try
{
    var laneId = CivilDoc.SubassemblyCollection.ImportStockSubassembly(
        assemblyName + "_Lane", "Subassembly.BasicLane", location);
    var lane = (Subassembly)Transaction.GetObject(laneId, OpenMode.ForWrite);
    lane.Side = SubassemblySideType.Right;
    assembly.AddSubassembly(laneId);
    subassemblies.Add(lane.Name);
    assembly.MirrorSubassembly(laneId);
    subassemblies.Add(lane.Name + " (mirrored)");
}
catch (System.Exception ex)
{
    subassemblyWarning = ex.GetType().Name + ": " + ex.Message;
}

var corridorId = CivilDoc.CorridorCollection.Add(
    corridorName, "Baseline", alignmentId, profileId, "Region", assemblyId);
var corridor = (Corridor)Transaction.GetObject(corridorId, OpenMode.ForWrite);
corridor.Rebuild();

var regions = 0;
foreach (Baseline baseline in corridor.Baselines) regions += baseline.BaselineRegions.Count;

return new {
    success = true,
    corridorName = corridor.Name,
    corridorHandle = corridor.Handle.ToString(),
    assemblyHandle = assembly.Handle.ToString(),
    baselines = corridor.Baselines.Count,
    regions,
    subassemblies,
    subassemblyWarning,
    outOfDate = corridor.IsOutOfDate
};
