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
      ("null primitives and enum retain JSON values", PrimitiveValues),
      ("existing JsonNode JsonElement and JsonDocument are bounded JSON", ExistingJsonValues),
      ("anonymous dictionary list and nested values retain ordinary shapes", OrdinaryShapes),
      ("all supported Autodesk geometry value types have explicit DTOs", AutodeskGeometryValues),
      ("ObjectId null and valid values have stable explicit shapes", ObjectIdValues),
      ("maximum depth is accepted and excess depth is rejected", DepthLimit),
      ("maximum collection size is accepted and excess items are rejected", CollectionLimit),
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
        "mode": 2
      }
      """);
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
    AssertSerializationError(() => ResultSerializer.Serialize(atLimit));
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
