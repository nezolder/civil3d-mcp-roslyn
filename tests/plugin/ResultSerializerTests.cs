using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

internal static class ResultSerializerTests
{
  public static void RunAll()
  {
    var tests = new (string Name, Action Run)[]
    {
      ("null and primitives retain JSON values and an enum returns its name", PrimitiveValues),
      ("doubles are rounded to ten significant digits and keep their whole part", RoundedNumbers),
      ("a result over the character limit is shortened from its largest list", SizeBudgetCutsLists),
      ("a long text and a wide table are shortened when no list is left", SizeBudgetCutsTextAndTables),
      ("existing JsonNode JsonElement and JsonDocument are bounded JSON", ExistingJsonValues),
      ("anonymous dictionary list and nested values retain ordinary shapes", OrdinaryShapes),
      ("all supported Autodesk geometry value types have explicit DTOs", AutodeskGeometryValues),
      ("ObjectId null and valid values have stable explicit shapes", ObjectIdValues),
      ("maximum depth is accepted and excess depth is rejected", DepthLimit),
      ("collections over the item limit keep their first items and report the cut", CollectionLimit),
      ("NaN and Infinity become null and are reported with their paths", NonFiniteValues),
      ("unknown values fail without ToString fallback", UnknownType),
      ("reference cycles fail deterministically", ReferenceCycle),
      ("DBObject values are rejected without reading database paths", DbObjectIsNotReflected),
    };

    foreach (var (name, run) in tests)
    {
      run();
      Console.WriteLine($"PASS {name}");
    }
  }

  private static void PrimitiveValues()
  {
    AssertJson(ResultSerializer.Serialize(null), "null");
    AssertJson(ResultSerializer.Serialize(new
    {
      Enabled = true,
      Text = "árvíztűrő",
      Letter = 'Z',
      Signed = long.MinValue,
      Unsigned = ulong.MaxValue,
      Ratio = 12.5m,
      Fingerprint = Guid.Parse("11111111-2222-4333-8444-555555555555"),
      Mode = SampleEnum.Two,
    }), """
      {
        "enabled": true,
        "text": "árvíztűrő",
        "letter": "Z",
        "signed": -9223372036854775808,
        "unsigned": 18446744073709551615,
        "ratio": 12.5,
        "fingerprint": "11111111-2222-4333-8444-555555555555",
        "mode": "Two"
      }
      """);

    // Combined flags read as their names; a value without a name stays a number, as text.
    AssertJson(ResultSerializer.Serialize(SampleFlags.Left | SampleFlags.Right), "\"Left, Right\"");
    AssertJson(ResultSerializer.Serialize((SampleEnum)7), "\"7\"");
  }

  private static void RoundedNumbers()
  {
    static string Text(object value) => ResultSerializer.Serialize(value)!.ToJsonString();

    Assert(Text(650123.456789012) == "650123.4568", "a coordinate keeps a tenth of a millimetre");
    Assert(Text(-650123.456789012) == "-650123.4568", "negative values round the same way");
    Assert(Text(0.1 + 0.2) == "0.3", "floating-point noise is removed");
    Assert(Text(99.99999999999997) == "100", "a value just below a whole number becomes it");
    Assert(Text(1.2345678901234567) == "1.23456789", "an angle in radians keeps ten digits");
    Assert(Text(1.0 / 3000.0) == "0.0003333333333", "a small value keeps ten significant digits, not four decimals");
    Assert(Text(12345678901.37) == "12345678901", "the whole part is never rounded");
    Assert(Text(1.2345678901234568e17) == "1.2345678901234568E+17", "a very large number is unchanged");
    Assert(Text(1.2345678901234567e-12) == "1.23456789E-12", "tiny values keep ten digits");
    Assert(Text(0.0) == "0" && Text(12.5) == "12.5" && Text(42.0) == "42", "short numbers are unchanged");
    // Decimals and floats are already as short as the caller wrote them.
    Assert(Text(12.123456789012345m) == "12.123456789012345", "a decimal is left as it is");
    Assert(Text(0.1f) == "0.1", "a float is left as it is");
    Assert(Text(new Point3d(650123.456789012, 237456.123456789, 101.23456789012))
      == "{\"x\":650123.4568,\"y\":237456.1235,\"z\":101.2345679}", "point coordinates are rounded too");
    // A number that must keep every digit is returned as text.
    Assert(Text(1.2345678901234567.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
      == "\"1.2345678901234567\"", "text is never rounded");
  }

  private static void SizeBudgetCutsLists()
  {
    // A result below the limit keeps its plain shape.
    var small = Enumerable.Range(0, 200).Select(index => new { station = index * 20.0, name = $"row {index}" }).ToList();
    Assert(ResultSerializer.Serialize(new { rows = small })!.AsObject()["truncated"] == null,
      "a small result must not be wrapped");

    var rows = Enumerable.Range(0, 900)
      .Select(index => new { station = index * 20.0, elevation = 100 + index * 0.125, name = $"sample line {index}" })
      .ToList();
    var wrapped = ResultSerializer.Serialize(new { drawing = "test", rows })!.AsObject();
    var result = wrapped["result"]!;
    var kept = result["rows"]!.AsArray().Count;
    Assert(result.ToJsonString().Length <= ResultSerializer.MaxResultCharacters, "the result must fit the limit");
    Assert(kept > 500 && kept < 900, $"the list keeps as many rows as fit, got {kept}");
    Assert(result["rows"]![0]!["station"]!.GetValue<double>() == 0
      && result["rows"]![kept - 1]!["name"]!.GetValue<string>() == $"sample line {kept - 1}",
      "the first rows are the ones kept");
    Assert(result["drawing"]!.GetValue<string>() == "test", "values outside the list are untouched");
    AssertJson(wrapped["truncated"],
      $"[{{\"path\":\"$.rows\",\"returned\":{kept},\"total\":900,\"reason\":\"size\"}}]");

    // Cut by the item limit first and by size afterwards: one entry, with the real total.
    var longRows = Enumerable.Range(0, 1500).Select(index => new { text = new string('a', 60), index }).ToList();
    var both = ResultSerializer.Serialize(longRows)!.AsObject();
    var entries = both["truncated"]!.AsArray();
    Assert(entries.Count == 1 && entries[0]!["path"]!.GetValue<string>() == "$"
      && entries[0]!["total"]!.GetValue<int>() == 1500
      && entries[0]!["returned"]!.GetValue<int>() == both["result"]!.AsArray().Count
      && entries[0]!["reason"]!.GetValue<string>() == "size",
      $"unexpected entry: {entries.ToJsonString()}");

    // Lists inside rows that are no longer returned are not reported.
    var nested = Enumerable.Range(0, 12)
      .Select(index => new { index, values = Enumerable.Range(0, 1200).ToList() })
      .ToList();
    var nestedWrapped = ResultSerializer.Serialize(new { rows = nested })!.AsObject();
    var nestedKept = nestedWrapped["result"]!["rows"]!.AsArray().Count;
    var paths = nestedWrapped["truncated"]!.AsArray().Select(entry => entry!["path"]!.GetValue<string>()).ToList();
    Assert(nestedKept >= 1 && nestedKept < 12, $"rows must be cut, kept {nestedKept}");
    Assert(paths.Contains("$.rows") && paths.Count == nestedKept + 1
      && Enumerable.Range(0, nestedKept).All(index => paths.Contains($"$.rows[{index}].values")),
      $"unexpected paths: {string.Join(", ", paths)}");
    Assert(nestedWrapped["result"]!.ToJsonString().Length <= ResultSerializer.MaxResultCharacters,
      "the nested result must fit the limit");

    // More values than the result may hold: lists stop, nothing fails, and the row in progress is whole.
    var wide = Enumerable.Range(0, 400)
      .Select(index => new { index, a = 1, b = 2, c = 3, d = 4, e = 5, f = 6, g = 7, h = 8, i = 9, j = 10, k = 11,
        l = 12, m = 13, n = 14, o = 15, p = 16, q = 17, r = 18, s = 19, t = 20, u = 21, v = 22, w = 23, x = 24,
        y = 25, z = 26, aa = 27, ab = 28 })
      .ToList();
    var wideWrapped = ResultSerializer.Serialize(wide)!.AsObject();
    var wideRows = wideWrapped["result"]!.AsArray();
    Assert(wideRows.Count > 0 && wideRows.Count < 400
      && wideRows[wideRows.Count - 1]!.AsObject().Count == 29,
      $"rows past the value limit are left out and the last kept row is complete; kept {wideRows.Count}");
    AssertJson(wideWrapped["truncated"],
      $"[{{\"path\":\"$\",\"returned\":{wideRows.Count},\"total\":400,\"reason\":\"size\"}}]");

    // One row that is itself too long: its own list is cut next.
    var single = ResultSerializer.Serialize(new[] { new { values = Enumerable.Range(0, 1000).Select(n => new string('b', 50) + n).ToList() } })!
      .AsObject();
    Assert(single["result"]!.AsArray().Count == 1
      && single["truncated"]![0]!["path"]!.GetValue<string>() == "$[0].values",
      $"unexpected cut: {single["truncated"]!.ToJsonString()}");
  }

  private static void SizeBudgetCutsTextAndTables()
  {
    var text = ResultSerializer.Serialize(new { log = new string('x', 100_000), count = 3 })!.AsObject();
    var keptText = text["result"]!["log"]!.GetValue<string>();
    Assert(text["result"]!.ToJsonString().Length <= ResultSerializer.MaxResultCharacters
      && keptText.Length > 30_000 && text["result"]!["count"]!.GetValue<int>() == 3,
      $"the text keeps what fits, got {keptText.Length}");
    AssertJson(text["truncated"],
      $"[{{\"path\":\"$.log\",\"returnedCharacters\":{keptText.Length},\"totalCharacters\":100000,\"reason\":\"size\"}}]");

    // Accented letters take six characters each in the reply; the cut allows for it.
    var accented = ResultSerializer.Serialize(new string('ő', 20_000))!.AsObject();
    Assert(accented["result"]!.ToJsonString().Length <= ResultSerializer.MaxResultCharacters
      && accented["result"]!.GetValue<string>().Length >= ResultSerializer.MinKeptTextCharacters,
      "an escaped text must fit the limit too");

    // A table keyed by name has no list to cut; it loses its last entries, not the record around it.
    var table = Enumerable.Range(0, 900).ToDictionary(index => $"alignment {index:000}", index => (object?)new { length = index * 12.5, layer = "C-ROAD" });
    var keyed = ResultSerializer.Serialize(new { drawing = "test", byName = table })!.AsObject();
    var keptEntries = keyed["result"]!["byName"]!.AsObject().Count;
    Assert(keyed["result"]!["drawing"]!.GetValue<string>() == "test" && keptEntries > 100 && keptEntries < 900,
      $"the table is cut, the record is not; kept {keptEntries}");
    AssertJson(keyed["truncated"],
      $"[{{\"path\":\"$.byName\",\"returned\":{keptEntries},\"total\":900,\"reason\":\"size\"}}]");
  }

  private static void ExistingJsonValues()
  {
    const string json = "{\"items\":[1,{\"ok\":true}],\"name\":\"json\"}";
    AssertJson(ResultSerializer.Serialize(JsonNode.Parse(json)), json);

    using var document = JsonDocument.Parse(json);
    AssertJson(ResultSerializer.Serialize(document), json);
    AssertJson(ResultSerializer.Serialize(document.RootElement), json);
  }

  private static void OrdinaryShapes()
  {
    var value = new
    {
      Title = "nested",
      Payload = new Dictionary<string, object?>
      {
        ["Items"] = new List<object?>
        {
          1,
          new { InnerValue = "ok" },
          null,
        },
      },
    };

    AssertJson(ResultSerializer.Serialize(value), """
      {
        "title": "nested",
        "payload": {
          "Items": [1, { "innerValue": "ok" }, null]
        }
      }
      """);
  }

  private static void AutodeskGeometryValues()
  {
    AssertJson(ResultSerializer.Serialize(new Handle(0x1ABC)), "\"1ABC\"");
    AssertJson(ResultSerializer.Serialize(new Point2d(1.25, -2.5)),
      "{\"x\":1.25,\"y\":-2.5}");
    AssertJson(ResultSerializer.Serialize(new Point3d(1, 2, 3)),
      "{\"x\":1,\"y\":2,\"z\":3}");
    AssertJson(ResultSerializer.Serialize(new Vector2d(-4, 5)),
      "{\"x\":-4,\"y\":5}");
    AssertJson(ResultSerializer.Serialize(new Vector3d(6, -7, 8)),
      "{\"x\":6,\"y\":-7,\"z\":8}");
  }

  private static void ObjectIdValues()
  {
    AssertJson(ResultSerializer.Serialize(ObjectId.Null), """
      { "isNull": true, "isValid": false, "handle": null }
      """);
    AssertJson(ResultSerializer.Serialize(new ObjectId(new Handle(0x2A))), """
      { "isNull": false, "isValid": true, "handle": "2A" }
      """);
  }

  private static void DepthLimit()
  {
    Assert(ResultSerializer.Serialize(Nest(ResultSerializer.MaxDepth)) != null,
      "value at maximum depth must serialize");
    AssertSerializationError(() => ResultSerializer.Serialize(
      Nest(ResultSerializer.MaxDepth + 1)
    ));
  }

  private static void CollectionLimit()
  {
    var atLimit = Enumerable.Range(0, ResultSerializer.MaxCollectionItems).ToList();
    Assert(ResultSerializer.Serialize(atLimit)?.AsArray().Count == ResultSerializer.MaxCollectionItems,
      "collection at item limit must serialize");

    atLimit.Add(ResultSerializer.MaxCollectionItems);
    var wrapped = ResultSerializer.Serialize(new { items = atLimit })!.AsObject();
    Assert(wrapped["result"]!["items"]!.AsArray().Count == ResultSerializer.MaxCollectionItems,
      "a long collection must keep exactly the item limit");
    AssertJson(wrapped["truncated"], "[{\"path\":\"$.items\",\"returned\":1000,\"total\":1001}]");
  }

  private static void NonFiniteValues()
  {
    // Finite results keep their plain shape: no wrapper, no extra key.
    AssertJson(ResultSerializer.Serialize(new { area = 12.5, point = new Point3d(1, 2, 3) }),
      "{\"area\":12.5,\"point\":{\"x\":1,\"y\":2,\"z\":3}}");

    // One undefined elevation must not discard the rest of a report.
    AssertJson(ResultSerializer.Serialize(new
    {
      name = "EG",
      min = double.NaN,
      rows = new object[]
      {
        new { station = 10.0, elevation = 101.25 },
        new { station = 20.0, elevation = double.PositiveInfinity },
      },
      point = new Point3d(1, 2, double.NaN),
      ratio = float.NegativeInfinity,
    }), """
      {
        "result": {
          "name": "EG",
          "min": null,
          "rows": [
            { "station": 10, "elevation": 101.25 },
            { "station": 20, "elevation": null }
          ],
          "point": { "x": 1, "y": 2, "z": null },
          "ratio": null
        },
        "nonFinite": {
          "count": 4,
          "paths": ["$.min", "$.rows[1].elevation", "$.point.z", "$.ratio"]
        }
      }
      """);

    AssertJson(ResultSerializer.Serialize(double.NaN),
      "{\"result\":null,\"nonFinite\":{\"count\":1,\"paths\":[\"$\"]}}");

    // Every value is counted; only the first paths are listed.
    var many = ResultSerializer.Serialize(Enumerable.Repeat(double.NaN, 50).ToList())!.AsObject();
    Assert(many["nonFinite"]!["count"]!.GetValue<int>() == 50, "all non-finite values must be counted");
    Assert(many["nonFinite"]!["paths"]!.AsArray().Count == ResultSerializer.MaxNonFinitePaths,
      "listed paths must be bounded");
    Assert(many["truncated"] == null, "the truncated key must appear only for a cut list");

    // A cut list and a non-finite value are reported side by side.
    var both = ResultSerializer.Serialize(new
    {
      bad = double.NaN,
      items = Enumerable.Range(0, ResultSerializer.MaxCollectionItems + 1).ToList(),
    })!.AsObject();
    AssertJson(both["truncated"], "[{\"path\":\"$.items\",\"returned\":1000,\"total\":1001}]");
    AssertJson(both["nonFinite"], "{\"count\":1,\"paths\":[\"$.bad\"]}");
  }

  private static void UnknownType()
  {
    var value = new StringDisguise();
    AssertSerializationError(() => ResultSerializer.Serialize(value));
    Assert(value.ToStringCalls == 0, "unknown values must not be converted through ToString");
  }

  private static void ReferenceCycle()
  {
    var value = new List<object?>();
    value.Add(value);
    AssertSerializationError(() => ResultSerializer.Serialize(value));
  }

  private static void DbObjectIsNotReflected()
  {
    var value = new SensitiveDbObject();
    var exception = AssertSerializationError(() => ResultSerializer.Serialize(value));
    Assert(value.FilenameReads == 0, "DBObject properties must not be reflected");
    Assert(!exception.Message.Contains(SensitiveDbObject.SecretPath, StringComparison.Ordinal),
      "serialization error must not expose Database.Filename");
  }

  private static object Nest(int levels)
  {
    object value = 1;
    for (var index = 0; index < levels; index++)
    {
      value = new List<object?> { value };
    }
    return value;
  }

  private static JsonRpcDispatchException AssertSerializationError(Action action)
  {
    try
    {
      action();
      throw new InvalidOperationException("Expected serialization error was not thrown.");
    }
    catch (JsonRpcDispatchException exception)
    {
      Assert(exception.Code == "CIVIL3D.RESULT_SERIALIZATION_FAILED",
        "serialization failure must use the structured error code");
      return exception;
    }
  }

  private static void AssertJson(JsonNode? actual, string expectedJson)
  {
    var expected = JsonNode.Parse(expectedJson);
    Assert(JsonNode.DeepEquals(actual, expected),
      $"JSON mismatch. Actual: {actual?.ToJsonString() ?? "null"}");
  }

  private static void Assert(bool condition, string message)
  {
    if (!condition) throw new InvalidOperationException(message);
  }

  private enum SampleEnum : ushort
  {
    Two = 2,
  }

  [Flags]
  private enum SampleFlags
  {
    Left = 1,
    Right = 2,
  }

  private sealed class StringDisguise
  {
    public int ToStringCalls { get; private set; }

    public override string ToString()
    {
      ToStringCalls++;
      return "disguised-result";
    }
  }

  private sealed class SensitiveDbObject : DBObject
  {
    public const string SecretPath = @"C:\Projects\Sensitive\Client.dwg";
    public int FilenameReads { get; private set; }

    public string Filename
    {
      get
      {
        FilenameReads++;
        return SecretPath;
      }
    }
  }
}
