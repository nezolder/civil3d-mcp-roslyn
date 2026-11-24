// Live smoke test fixture: a siteless alignment from the fixture polyline,
// with the first alignment style and label set of the drawing. The polyline
// stays. (The catalog recipe create_alignment_from_polyline asks for office
// style names, which a plain template does not have.)
var polylineHandle = "POLYLINE_HANDLE"; // smoke-param
var alignmentName = "ALIGNMENT_NAME"; // smoke-param
var styleName = "STYLE_NAME"; // smoke-param
var labelSetName = "LABEL_SET_NAME"; // smoke-param

var polylineId = Database.GetObjectId(false, new Handle(System.Convert.ToInt64(polylineHandle, 16)), 0);
var polyline = Transaction.GetObject(polylineId, OpenMode.ForRead) as Polyline;
if (polyline == null || polyline.IsErased)
    throw new InvalidOperationException("The fixture polyline was not found.");

var options = new PolylineOptions
{
    PlineId = polylineId,
    EraseExistingEntities = false,
    AddCurvesBetweenTangents = false
};
var alignmentId = Alignment.Create(
    CivilDoc,
    options,
    alignmentName,
    ObjectId.Null,
    polyline.LayerId,
    CivilDoc.Styles.AlignmentStyles[styleName],
    CivilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles[labelSetName]);
var alignment = (Alignment)Transaction.GetObject(alignmentId, OpenMode.ForRead);

return new {
    success = true,
    name = alignment.Name,
    handle = alignment.Handle.ToString(),
    style = alignment.StyleName,
    length = alignment.Length,
    startStation = alignment.StartingStation,
    endStation = alignment.EndingStation,
    entityCount = alignment.Entities.Count
};
