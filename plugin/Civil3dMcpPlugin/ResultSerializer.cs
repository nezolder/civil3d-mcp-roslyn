// Provenance: the limited Autodesk type mapping was evaluated and adapted for Civil 3D 2025
// from SantosSjba/mcp-to-c3d @ 6a77cfbb8d0ccd468bf1a59f8252f42aadbf3ab7; the repo MIT LICENSE remains.
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Civil3DMcpPlugin;

/// <summary>
/// Converts the raw Roslyn return value to a small, bounded JSON-safe DTO.
/// Only explicitly supported Autodesk value types and ordinary JSON shapes are allowed.
/// </summary>
internal static class ResultSerializer
{
  internal const int MaxDepth = 8;
  internal const int MaxCollectionItems = 1_000;
  internal const int MaxTotalNodes = 10_000;

  public static JsonNode? Serialize(object? value)
  {
    try
    {
      return new SerializationState().Convert(value, depth: 0);
    }
    catch (JsonRpcDispatchException)
    {
      throw;
    }
    catch
    {
      throw SerializationError(
        $"Result type '{GetTypeName(value)}' could not be serialized safely."
      );
    }
  }

  private static string GetTypeName(object? value)
    => value?.GetType().FullName ?? "null";

  private static JsonRpcDispatchException SerializationError(string message)
    => new("CIVIL3D.RESULT_SERIALIZATION_FAILED", message);

  private sealed class SerializationState
  {
    private readonly HashSet<object> _activeReferences = new(ReferenceEqualityComparer.Instance);
    private int _totalNodes;

    public JsonNode? Convert(object? value, int depth)
    {
      EnsureDepth(depth);
      ConsumeNode();

      return value switch
      {
        null => null,
        JsonNode node => ConvertJsonNode(node, depth),
        JsonDocument document => ConvertJsonElement(document.RootElement, depth),
        JsonElement element => ConvertJsonElement(element, depth),
        bool boolean => JsonValue.Create(boolean),
        string text => JsonValue.Create(text),
        char character => JsonValue.Create(character.ToString()),
        byte number => JsonValue.Create(number),
        sbyte number => JsonValue.Create(number),
        short number => JsonValue.Create(number),
        ushort number => JsonValue.Create(number),
        int number => JsonValue.Create(number),
        uint number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        ulong number => JsonValue.Create(number),
        float number => CreateFiniteNumber(number),
        double number => CreateFiniteNumber(number),
        decimal number => JsonValue.Create(number),
        Guid guid => JsonValue.Create(guid.ToString("D", CultureInfo.InvariantCulture)),
        Enum enumValue => ConvertEnum(enumValue),
        ObjectId objectId => ConvertObjectId(objectId),
        Handle handle => ConvertHandle(handle),
        Point2d point => ConvertPoint2d(point),
        Point3d point => ConvertPoint3d(point),
        Vector2d vector => ConvertVector2d(vector),
        Vector3d vector => ConvertVector3d(vector),
        DBObject => throw SerializationError(
          "Returning AutoCAD DBObject instances is not supported; return explicit scalar values or ObjectId instead."
        ),
        IDictionary dictionary => ConvertDictionary(dictionary, depth),
        IList list => ConvertList(list, depth),
        _ when IsAnonymousType(value.GetType()) => ConvertAnonymousObject(value, depth),
        _ => throw SerializationError(
          $"Result type '{GetTypeName(value)}' is not supported."
        ),
      };
    }

    private JsonNode ConvertJsonNode(JsonNode node, int depth)
    {
      return node switch
      {
        JsonObject jsonObject => TrackReference(jsonObject, () =>
          ConvertJsonObject(jsonObject, depth)),
        JsonArray jsonArray => TrackReference(jsonArray, () =>
          ConvertJsonArray(jsonArray, depth)),
        JsonValue jsonValue => ConvertJsonValue(jsonValue, depth),
        _ => throw SerializationError(
          $"JSON node type '{node.GetType().FullName}' is not supported."
        ),
      };
    }

    private JsonNode ConvertJsonObject(JsonObject source, int depth)
    {
      EnsureCollectionSize(source.Count);
      var result = new JsonObject();
      foreach (var property in source)
      {
        AddProperty(result, property.Key, Convert(property.Value, depth + 1));
      }
      return result;
    }

    private JsonNode ConvertJsonArray(JsonArray source, int depth)
    {
      EnsureCollectionSize(source.Count);
      var result = new JsonArray();
      foreach (var item in source)
      {
        result.Add(Convert(item, depth + 1));
      }
      return result;
    }

    private JsonNode ConvertJsonValue(JsonValue value, int depth)
    {
      if (value.TryGetValue<JsonElement>(out var element))
      {
        return ConvertJsonElement(element, depth);
      }
      if (value.TryGetValue<bool>(out var boolean)) return JsonValue.Create(boolean)!;
      if (value.TryGetValue<string>(out var text)) return JsonValue.Create(text)!;
      if (value.TryGetValue<char>(out var character)) return JsonValue.Create(character.ToString())!;
      if (value.TryGetValue<byte>(out var byteValue)) return JsonValue.Create(byteValue)!;
      if (value.TryGetValue<sbyte>(out var sbyteValue)) return JsonValue.Create(sbyteValue)!;
      if (value.TryGetValue<short>(out var shortValue)) return JsonValue.Create(shortValue)!;
      if (value.TryGetValue<ushort>(out var ushortValue)) return JsonValue.Create(ushortValue)!;
      if (value.TryGetValue<int>(out var intValue)) return JsonValue.Create(intValue)!;
      if (value.TryGetValue<uint>(out var uintValue)) return JsonValue.Create(uintValue)!;
      if (value.TryGetValue<long>(out var longValue)) return JsonValue.Create(longValue)!;
      if (value.TryGetValue<ulong>(out var ulongValue)) return JsonValue.Create(ulongValue)!;
      if (value.TryGetValue<float>(out var floatValue)) return CreateFiniteNumber(floatValue);
      if (value.TryGetValue<double>(out var doubleValue)) return CreateFiniteNumber(doubleValue);
      if (value.TryGetValue<decimal>(out var decimalValue)) return JsonValue.Create(decimalValue)!;
      if (value.TryGetValue<Guid>(out var guidValue))
      {
        return JsonValue.Create(guidValue.ToString("D", CultureInfo.InvariantCulture))!;
      }

      throw SerializationError("JSON values must contain only supported JSON primitives.");
    }

    private JsonNode ConvertJsonElement(JsonElement element, int depth)
    {
      switch (element.ValueKind)
      {
        case JsonValueKind.Null:
          return JsonValue.Create((string?)null)!;
        case JsonValueKind.True:
          return JsonValue.Create(true)!;
        case JsonValueKind.False:
          return JsonValue.Create(false)!;
        case JsonValueKind.String:
          return JsonValue.Create(element.GetString())!;
        case JsonValueKind.Number:
          return JsonNode.Parse(element.GetRawText())!;
        case JsonValueKind.Object:
        {
          var result = new JsonObject();
          var count = 0;
          foreach (var property in element.EnumerateObject())
          {
            EnsureCollectionSize(++count);
            AddProperty(result, property.Name, Convert(property.Value, depth + 1));
          }
          return result;
        }
        case JsonValueKind.Array:
        {
          EnsureCollectionSize(element.GetArrayLength());
          var result = new JsonArray();
          foreach (var item in element.EnumerateArray())
          {
            result.Add(Convert(item, depth + 1));
          }
          return result;
        }
        default:
          throw SerializationError(
            $"JSON value kind '{element.ValueKind}' is not supported."
          );
      }
    }

    private JsonNode ConvertDictionary(IDictionary source, int depth)
    {
      EnsureCollectionSize(source.Count);
      return TrackReference(source, () =>
      {
        var result = new JsonObject();
        foreach (DictionaryEntry entry in source)
        {
          if (entry.Key is not string key)
          {
            throw SerializationError("Dictionary result keys must be strings.");
          }
          AddProperty(result, key, Convert(entry.Value, depth + 1));
        }
        return result;
      });
    }

    private JsonNode ConvertList(IList source, int depth)
    {
      EnsureCollectionSize(source.Count);
      return TrackReference(source, () =>
      {
        var result = new JsonArray();
        for (var index = 0; index < source.Count; index++)
        {
          result.Add(Convert(source[index], depth + 1));
        }
        return result;
      });
    }

    private JsonNode ConvertAnonymousObject(object source, int depth)
    {
      return TrackReference(source, () =>
      {
        var properties = source.GetType()
          .GetProperties(BindingFlags.Instance | BindingFlags.Public)
          .Where(property => property.GetIndexParameters().Length == 0)
          .OrderBy(property => property.MetadataToken)
          .ToArray();
        EnsureCollectionSize(properties.Length);

        var result = new JsonObject();
        foreach (var property in properties)
        {
          object? propertyValue;
          try
          {
            propertyValue = property.GetValue(source);
          }
          catch
          {
            throw SerializationError(
              $"Anonymous result property '{property.Name}' could not be read safely."
            );
          }

          var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
          AddProperty(result, jsonName, Convert(propertyValue, depth + 1));
        }
        return result;
      });
    }

    private static JsonNode ConvertEnum(Enum value)
    {
      return Type.GetTypeCode(Enum.GetUnderlyingType(value.GetType())) switch
      {
        TypeCode.SByte => JsonValue.Create(System.Convert.ToSByte(value, CultureInfo.InvariantCulture))!,
        TypeCode.Byte => JsonValue.Create(System.Convert.ToByte(value, CultureInfo.InvariantCulture))!,
        TypeCode.Int16 => JsonValue.Create(System.Convert.ToInt16(value, CultureInfo.InvariantCulture))!,
        TypeCode.UInt16 => JsonValue.Create(System.Convert.ToUInt16(value, CultureInfo.InvariantCulture))!,
        TypeCode.Int32 => JsonValue.Create(System.Convert.ToInt32(value, CultureInfo.InvariantCulture))!,
        TypeCode.UInt32 => JsonValue.Create(System.Convert.ToUInt32(value, CultureInfo.InvariantCulture))!,
        TypeCode.Int64 => JsonValue.Create(System.Convert.ToInt64(value, CultureInfo.InvariantCulture))!,
        TypeCode.UInt64 => JsonValue.Create(System.Convert.ToUInt64(value, CultureInfo.InvariantCulture))!,
        _ => throw SerializationError(
          $"Enum result type '{value.GetType().FullName}' has an unsupported underlying type."
        ),
      };
    }

    private static JsonNode ConvertObjectId(ObjectId value)
    {
      var isNull = value.IsNull;
      var isValid = value.IsValid;
      return new JsonObject
      {
        ["isNull"] = isNull,
        ["isValid"] = isValid,
        ["handle"] = !isNull && isValid ? value.Handle.ToString() : null,
      };
    }

    private static JsonNode ConvertHandle(Handle value)
      => JsonValue.Create(value.ToString())!;

    private static JsonNode ConvertPoint2d(Point2d value)
      => new JsonObject
      {
        ["x"] = CreateFiniteNumber(value.X),
        ["y"] = CreateFiniteNumber(value.Y),
      };

    private static JsonNode ConvertPoint3d(Point3d value)
      => new JsonObject
      {
        ["x"] = CreateFiniteNumber(value.X),
        ["y"] = CreateFiniteNumber(value.Y),
        ["z"] = CreateFiniteNumber(value.Z),
      };

    private static JsonNode ConvertVector2d(Vector2d value)
      => new JsonObject
      {
        ["x"] = CreateFiniteNumber(value.X),
        ["y"] = CreateFiniteNumber(value.Y),
      };

    private static JsonNode ConvertVector3d(Vector3d value)
      => new JsonObject
      {
        ["x"] = CreateFiniteNumber(value.X),
        ["y"] = CreateFiniteNumber(value.Y),
        ["z"] = CreateFiniteNumber(value.Z),
      };

    private static JsonNode CreateFiniteNumber(float value)
      => float.IsFinite(value)
        ? JsonValue.Create(value)!
        : throw SerializationError("Non-finite floating-point result values are not supported.");

    private static JsonNode CreateFiniteNumber(double value)
      => double.IsFinite(value)
        ? JsonValue.Create(value)!
        : throw SerializationError("Non-finite floating-point result values are not supported.");

    private JsonNode TrackReference(object value, Func<JsonNode> convert)
    {
      if (!_activeReferences.Add(value))
      {
        throw SerializationError("Result contains a reference cycle.");
      }

      try
      {
        return convert();
      }
      finally
      {
        _activeReferences.Remove(value);
      }
    }

    private static bool IsAnonymousType(Type type)
      => type.IsSealed
        && type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        && type.Name.Contains("AnonymousType", StringComparison.Ordinal)
        && (type.Name.StartsWith("<>", StringComparison.Ordinal)
          || type.Name.StartsWith("VB$", StringComparison.Ordinal));

    private static void AddProperty(JsonObject target, string name, JsonNode? value)
    {
      if (target.ContainsKey(name))
      {
        throw SerializationError($"Result contains duplicate JSON property '{name}'.");
      }
      target.Add(name, value);
    }

    private static void EnsureDepth(int depth)
    {
      if (depth > MaxDepth)
      {
        throw SerializationError($"Result exceeded the maximum depth of {MaxDepth}.");
      }
    }

    private static void EnsureCollectionSize(int count)
    {
      if (count > MaxCollectionItems)
      {
        throw SerializationError(
          $"Result collection exceeded the maximum of {MaxCollectionItems} items."
        );
      }
    }

    private void ConsumeNode()
    {
      _totalNodes++;
      if (_totalNodes > MaxTotalNodes)
      {
        throw SerializationError(
          $"Result exceeded the maximum object graph size of {MaxTotalNodes} nodes."
        );
      }
    }
  }
}
