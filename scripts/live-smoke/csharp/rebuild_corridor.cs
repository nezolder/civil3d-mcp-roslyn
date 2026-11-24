// Live smoke test probe: rebuilds the fixture corridor.
var corridorHandle = "CORRIDOR_HANDLE"; // smoke-param

var corridorId = Database.GetObjectId(false, new Handle(System.Convert.ToInt64(corridorHandle, 16)), 0);
var corridor = (Corridor)Transaction.GetObject(corridorId, OpenMode.ForWrite);
corridor.Rebuild();
return new { success = true, corridor = corridor.Name, outOfDate = corridor.IsOutOfDate };
