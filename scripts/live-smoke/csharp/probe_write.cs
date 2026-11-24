// Live smoke test probe: adds one line and sets the alignment description,
// then optionally fails on purpose, so that the receipt and the rollback of a
// failed script can be checked.
var alignmentHandle = "ALIGNMENT_HANDLE"; // smoke-param
var marker = "MARKER"; // smoke-param
var x1 = 0.0; // smoke-param
var y1 = 0.0; // smoke-param
var x2 = 0.0; // smoke-param
var y2 = 0.0; // smoke-param
var failAfterChange = false; // smoke-param

var alignmentId = Database.GetObjectId(false, new Handle(System.Convert.ToInt64(alignmentHandle, 16)), 0);
var alignment = (Alignment)Transaction.GetObject(alignmentId, OpenMode.ForWrite);
alignment.Description = marker;

var modelSpace = (BlockTableRecord)Transaction.GetObject(
    SymbolUtilityServices.GetBlockModelSpaceId(Database), OpenMode.ForWrite);
var line = new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0));
modelSpace.AppendEntity(line);
Transaction.AddNewlyCreatedDBObject(line, true);

if (failAfterChange)
    throw new InvalidOperationException("Planned failure of the live smoke test after a change.");

return new { success = true, lineHandle = line.Handle.ToString(), alignment = alignment.Name, marker };
