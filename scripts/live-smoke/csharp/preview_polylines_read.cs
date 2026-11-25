// Live smoke test probe, read only: whether the preview layer exists and how
// many 3D polylines are in model space, with the drawing state.
var layerName = "LAYER"; // smoke-param

var layers = (LayerTable)Transaction.GetObject(Database.LayerTableId, OpenMode.ForRead);
var modelSpace = (BlockTableRecord)Transaction.GetObject(
    SymbolUtilityServices.GetBlockModelSpaceId(Database), OpenMode.ForRead);
var polylines = 0;
foreach (ObjectId id in modelSpace)
{
    if (Transaction.GetObject(id, OpenMode.ForRead) is Polyline3d) polylines++;
}

return new {
    layerExists = layers.Has(layerName),
    polylines,
    dbmod = System.Convert.ToInt32(Application.GetSystemVariable("DBMOD"))
};
