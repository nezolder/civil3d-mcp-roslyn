---
name: station_offset
category: alignments
description: Convert between station+offset and X,Y coordinates along an alignment
requires_write: false
aliases: ["nyomvonal szelvény-külpont átszámítása koordinátára", "alignment station offset to XY and back", "szelvény és külpont koordináta lekérdezés"]
workflow_tags: ["coordinate_conversion"]
tested_civil_version: "2025"
validation_summary: "Offline: 2025 API compilation. Recipe-specific live coverage is not recorded here; verify the required case before use."
parameters:
  - name: alignmentName
    type: string
    required: true
    description: Exact alignment name
  - name: station
    type: double
    required: false
    description: Station to convert to a point; leave out when x and y are given
  - name: offset
    type: double
    required: false
    description: Offset from the centerline for the station-to-point conversion (default 0)
  - name: x
    type: double
    required: false
    description: X of the point to convert to station and offset; needs y
  - name: y
    type: double
    required: false
    description: Y of the point to convert to station and offset; needs x
---

## Code Template

```csharp
var alignmentName = "";
var station = double.NaN;
var offset = 0.0;
var x = double.NaN;
var y = double.NaN;

if (string.IsNullOrWhiteSpace(alignmentName)) return new { error = "alignmentName is required" };
var hasStation = !double.IsNaN(station);
var hasPoint = !double.IsNaN(x) && !double.IsNaN(y);
if (double.IsNaN(x) != double.IsNaN(y)) return new { error = "x and y must be given together" };
if (hasStation == hasPoint)
    return new { error = "Give either station (with optional offset) or x and y, not both and not neither" };
if (double.IsInfinity(station) || double.IsNaN(offset) || double.IsInfinity(offset)
    || double.IsInfinity(x) || double.IsInfinity(y))
    return new { error = "station, offset, x and y must be finite numbers" };

Alignment alignment = null;
foreach (ObjectId id in CivilDoc.GetAlignmentIds())
{
    var candidate = Transaction.GetObject(id, OpenMode.ForRead) as Alignment;
    if (candidate != null && candidate.Name.Equals(alignmentName, StringComparison.OrdinalIgnoreCase))
    { alignment = candidate; break; }
}
if (alignment == null) return new { error = "Alignment not found" };

if (hasStation)
{
    // Station and offset to a point.
    double easting = 0, northing = 0;
    alignment.PointLocation(station, offset, ref easting, ref northing);
    return new {
        alignmentName = alignment.Name, conversion = "stationToPoint",
        station, offset, x = easting, y = northing
    };
}

// A point to its station and offset.
double foundStation = 0, foundOffset = 0;
alignment.StationOffset(x, y, ref foundStation, ref foundOffset);
return new {
    alignmentName = alignment.Name, conversion = "pointToStation",
    x, y, station = foundStation, offset = foundOffset
};
```

## Usage Notes
- Give `station` (and optionally `offset`) to get a point, or `x` and `y` to get the station and offset; one call converts in one direction
- Offset is negative to the left and positive to the right of the alignment
- Coordinates are in the drawing's coordinate system, without conversion
- A station outside the alignment, or a point that cannot be projected onto it, raises a Civil 3D exception that is reported as the error
