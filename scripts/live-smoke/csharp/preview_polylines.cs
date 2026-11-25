// Live smoke test probe for previews: creates a layer and 3D polylines with
// several vertices each. Run as a preview, nothing of it may stay. (A 3D
// polyline brings parts the drawing makes by itself: its vertices and an end
// marker. In a field test a preview of this kind was reported as not rolled
// back although everything was gone.)
var layerName = "LAYER"; // smoke-param
var polylineCount = 3; // smoke-param
var x0 = 0.0; // smoke-param
var y0 = 0.0; // smoke-param

var layers = (LayerTable)Transaction.GetObject(Database.LayerTableId, OpenMode.ForWrite);
if (layers.Has(layerName))
    return new { success = false, error = "the layer exists before the preview: " + layerName };
var layer = new LayerTableRecord { Name = layerName };
layers.Add(layer);
Transaction.AddNewlyCreatedDBObject(layer, true);

var modelSpace = (BlockTableRecord)Transaction.GetObject(
    SymbolUtilityServices.GetBlockModelSpaceId(Database), OpenMode.ForWrite);
var vertices = 0;
for (var index = 0; index < polylineCount; index++)
{
    var points = new Point3dCollection();
    for (var step = 0; step < 5; step++)
        points.Add(new Point3d(x0 + step * 10.0, y0 + index * 5.0, 100.0 + step * 0.1));
    var polyline = new Polyline3d(Poly3dType.SimplePoly, points, false) { Layer = layerName };
    modelSpace.AppendEntity(polyline);
    Transaction.AddNewlyCreatedDBObject(polyline, true);
    vertices += points.Count;
}

return new { success = true, polylines = polylineCount, vertices };
