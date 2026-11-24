// Live smoke test probe, read only: how many lines lie on the given segment
// and what the alignment description is, with the drawing state.
var alignmentHandle = "ALIGNMENT_HANDLE"; // smoke-param
var x1 = 0.0; // smoke-param
var y1 = 0.0; // smoke-param
var x2 = 0.0; // smoke-param
var y2 = 0.0; // smoke-param

var start = new Point3d(x1, y1, 0);
var end = new Point3d(x2, y2, 0);
var tolerance = new Tolerance(1e-6, 1e-6);
var modelSpace = (BlockTableRecord)Transaction.GetObject(
    SymbolUtilityServices.GetBlockModelSpaceId(Database), OpenMode.ForRead);
var lines = 0;
foreach (ObjectId id in modelSpace)
{
    if (!(Transaction.GetObject(id, OpenMode.ForRead) is Line line)) continue;
    if (line.StartPoint.IsEqualTo(start, tolerance) && line.EndPoint.IsEqualTo(end, tolerance)) lines++;
}
var alignmentId = Database.GetObjectId(false, new Handle(System.Convert.ToInt64(alignmentHandle, 16)), 0);
var alignment = (Alignment)Transaction.GetObject(alignmentId, OpenMode.ForRead);

return new {
    lines,
    description = alignment.Description,
    databaseFilename = Database.Filename,
    dbmod = System.Convert.ToInt32(Application.GetSystemVariable("DBMOD"))
};
