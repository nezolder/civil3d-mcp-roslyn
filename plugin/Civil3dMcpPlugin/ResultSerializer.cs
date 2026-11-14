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
/// A list longer than <see cref="MaxCollectionItems"/> keeps its first items, and the
/// whole result is then wrapped as <c>{"result": ..., "truncated": [...]}</c> so the
/// cut is never silent. Lists also stop once <see cref="MaxTotalNodes"/> values were converted. NaN and Infinity have no JSON form: each becomes null and the
/// wrapper lists where, as <c>"nonFinite": {"count": n, "paths": [...]}</c>, so one bad
/// number does not discard an otherwise useful result.
/// Replies are also kept small for the reader: a double is rounded to
/// <see cref="SignificantDigits"/> significant digits (its whole part is never
/// touched), an enum is returned by name, and a result longer than
/// <see cref="MaxResultCharacters"/> characters is shortened from the end of its
/// largest lists and texts, each cut listed under <c>truncated</c> with
/// <c>"reason": "size"</c>.
/// </summary>
internal static class ResultSerializer
{
  internal const int MaxDepth = 8;
  internal const int MaxCollectionItems = 1_000;
  /// <summary>
  /// Values converted before lists stop taking further items. A list that
  /// stops here is reported like any other cut, so a wide table returns its
  /// first rows instead of failing as a whole.
  /// </summary>
  internal const int MaxTotalNodes = 10_000;

  /// <summary>
  /// Hard stop for a result that keeps growing without lists (nested objects
  /// and dictionaries only); such a result is refused.
  /// </summary>
  internal const int MaxTotalNodesHard = 2 * MaxTotalNodes;
  internal const int MaxNonFinitePaths = 20;

  /// <summary>
  /// Longest result, in characters of JSON text. About 10 000 to 16 000 tokens
  /// of number-heavy JSON: enough for a few hundred table rows, and below the
  /// size at which clients start to drop or refuse a tool reply.
  /// </summary>
  internal const int MaxResultCharacters = 40_000;

  /// <summary>
  /// Digits kept in a double. Ten digits are a tenth of a millimetre on a
  /// seven-digit coordinate and far finer than any angle or slope a drawing
  /// holds; the seventeen digits of a raw double are noise that costs tokens.
  /// </summary>
  internal const int SignificantDigits = 10;

  /// <summary>A text is never cut below this many characters.</summary>
  internal const int MinKeptTextCharacters = 200;

  public static JsonNode? Serialize(object? value) => Serialize(value, null);

  /// <summary>
  /// As <see cref="Serialize(object?)"/>; image files the script returned with
  /// <c>ImageFile(path)</c> are read into <paramref name="images"/> and leave
  /// only a small reference in the JSON. Without a list to put them in, a
  /// result that holds an image is refused.
  /// </summary>
  internal static JsonNode? Serialize(object? value, List<ScriptImagePayload>? images)
  {
    try
    {
      var state = new SerializationState(images);
      var result = state.Convert(value, depth: 0, path: "$");
      result = ResultSizeBudget.Fit(result, state.Truncations);
      if (state.Truncations.Count == 0 && state.NonFiniteCount == 0) return result;
      var wrapped = new JsonObject { ["result"] = result };
      if (state.Truncations.Count > 0)
      {
        wrapped["truncated"] = new JsonArray(state.Truncations.ToArray());
      }
      if (state.NonFiniteCount > 0)
      {
        wrapped["nonFinite"] = new JsonObject
        {
          ["count"] = state.NonFiniteCount,
          ["paths"] = new JsonArray(
            state.NonFinitePaths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()),
        };
      }
      return wrapped;
    }
    catch (Exception ex) when (ex is JsonRpcDispatchException or OperationCanceledException)
    {
      // Cancellation comes from a loop checkpoint in caller code; the executor reports it.
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

  internal static JsonRpcDispatchException SerializationError(string message)
    => new("CIVIL3D.RESULT_SERIALIZATION_FAILED", message);

  /// <summary>
  /// Rounds to <see cref="SignificantDigits"/> significant digits without ever
  /// changing the whole part: 650123.456789012 becomes 650123.4568,
  /// 0.30000000000000004 becomes 0.3, 12345678901.37 becomes 12345678901.
  /// A script that needs every digit returns the number as text.
  /// </summary>
  internal static double RoundNumber(double value)
  {
    if (value == 0 || !double.IsFinite(value)) return value;
    var magnitude = Math.Abs(value);
    // From ten whole digits on there is no room left for decimals.
    if (magnitude >= 1e9) return magnitude >= 1e15 ? value : Math.Round(value);
    var rounded = double.Parse(
      value.ToString("G" + SignificantDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
      NumberStyles.Float,
      CultureInfo.InvariantCulture);
    return rounded;
  }

  private sealed class SerializationState
  {
    private readonly HashSet<object> _activeReferences = new(ReferenceEqualityComparer.Instance);
    private readonly List<ScriptImagePayload>? _images;
    private long _imageBytes;
    private int _totalNodes;

    public SerializationState(List<ScriptImagePayload>? images = null) => _images = images;

    /// <summary>One entry per list that was cut to <see cref="MaxCollectionItems"/>.</summary>
    public List<JsonNode> Truncations { get; } = new();

    /// <summary>NaN/Infinity values replaced by null, and where the first ones were.</summary>
    public int NonFiniteCount { get; private set; }
    public List<string> NonFinitePaths { get; } = new();

    public JsonNode? Convert(object? value, int depth, string path)
    {
      EnsureDepth(depth);
      ConsumeNode();

      return value switch
      {
        null => null,
        JsonNode node => ConvertJsonNode(node, depth, path),
        JsonDocument document => ConvertJsonElement(document.RootElement, depth, path),
        JsonElement element => ConvertJsonElement(element, depth, path),
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
        float number => CreateFiniteNumber(number, path),
        double number => CreateFiniteNumber(number, path),
        decimal number => JsonValue.Create(number),
        Guid guid => JsonValue.Create(guid.ToString("D", CultureInfo.InvariantCulture)),
        DateTime dateTime => JsonValue.Create(dateTime.ToString("o", CultureInfo.InvariantCulture)),
        DateTimeOffset dateTimeOffset => JsonValue.Create(dateTimeOffset.ToString("o", CultureInfo.InvariantCulture)),
        DateOnly date => JsonValue.Create(date.ToString("o", CultureInfo.InvariantCulture)),
        TimeOnly time => JsonValue.Create(time.ToString("o", CultureInfo.InvariantCulture)),
        TimeSpan timeSpan => JsonValue.Create(timeSpan.ToString("c", CultureInfo.InvariantCulture)),
        Enum enumValue => ConvertEnum(enumValue),
        ObjectId objectId => ConvertObjectId(objectId),
        Handle handle => ConvertHandle(handle),
        Point2d point => ConvertXy(point.X, point.Y, path),
        Point3d point => ConvertXyz(point.X, point.Y, point.Z, path),
        Vector2d vector => ConvertXy(vector.X, vector.Y, path),
        Vector3d vector => ConvertXyz(vector.X, vector.Y, vector.Z, path),
        DBObject => throw SerializationError(
          "Returning AutoCAD DBObject instances is not supported; return explicit scalar values or ObjectId instead."
        ),
        ScriptImage image => ConvertImage(image),
        IDictionary dictionary => ConvertDictionary(dictionary, depth, path),
        IList list => ConvertList(list, depth, path),
        ITuple tuple => ConvertTuple(tuple, depth, path),
        IEnumerable sequence => ConvertSequence(sequence, depth, path),
        _ when IsAnonymousType(value.GetType()) || IsScriptDefinedType(value.GetType())
          => ConvertObjectMembers(value, depth, path),
        _ => throw SerializationError(
          $"Result type '{GetTypeName(value)}' is not supported."
        ),
      };
    }

    /// <summary>
    /// Reads the file now, while the script's work is still open, and keeps
    /// only a reference in the JSON. Every refusal names the file and the
    /// limit, because the script can usually plot a smaller picture.
    /// </summary>
    private JsonNode ConvertImage(ScriptImage image)
    {
      if (_images == null)
      {
        throw SerializationError("An image can only be returned as part of a script result.");
      }
      if (_images.Count >= ScriptImages.MaxImagesPerResult)
      {
        throw SerializationError(
          $"A result can hold at most {ScriptImages.MaxImagesPerResult} images; return the others in further calls.");
      }
      var path = image.Path;
      if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
      {
        throw SerializationError("ImageFile needs the full path of a PNG or JPEG file.");
      }

      byte[] data;
      try
      {
        var file = new FileInfo(path);
        if (!file.Exists) throw SerializationError($"Image file not found: {path}");
        if (file.Length > ScriptImages.MaxImageBytes)
        {
          throw SerializationError(
            $"Image file '{path}' has {file.Length} bytes; the limit is {ScriptImages.MaxImageBytes}. Plot a smaller picture.");
        }
        data = File.ReadAllBytes(path);
      }
      catch (JsonRpcDispatchException)
      {
        throw;
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
      {
        throw SerializationError($"Image file '{path}' could not be read: {ex.Message}");
      }

      var info = ScriptImages.Inspect(data)
        ?? throw SerializationError($"Image file '{path}' is neither a PNG nor a JPEG file.");
      if (_imageBytes + data.Length > ScriptImages.MaxTotalImageBytes)
      {
        throw SerializationError(
          $"The images of one result may hold {ScriptImages.MaxTotalImageBytes} bytes together; return fewer or smaller pictures.");
      }
      _imageBytes += data.Length;
      _images.Add(new ScriptImagePayload(info.MimeType, data));

      var reference = new JsonObject
      {
        ["image"] = _images.Count,
        ["mimeType"] = info.MimeType,
        ["bytes"] = data.Length,
      };
      if (info.Width is int width && info.Height is int height)
      {
        reference["width"] = width;
        reference["height"] = height;
      }
      return reference;
    }

    private JsonNode? ConvertJsonNode(JsonNode node, int depth, string path)
    {
      return node switch
      {
        JsonObject jsonObject => TrackReference(jsonObject, () =>
          ConvertJsonObject(jsonObject, depth, path)),
        JsonArray jsonArray => TrackReference(jsonArray, () =>
          ConvertJsonArray(jsonArray, depth, path)),
        JsonValue jsonValue => ConvertJsonValue(jsonValue, depth, path),
        _ => throw SerializationError(
          $"JSON node type '{node.GetType().FullName}' is not supported."
        ),
      };
    }

    private JsonNode ConvertJsonObject(JsonObject source, int depth, string path)
    {
      EnsureCollectionSize(source.Count);
      var result = new JsonObject();
      foreach (var property in source)
      {
        AddProperty(result, property.Key, Convert(property.Value, depth + 1, PropertyPath(path, property.Key)));
      }
      return result;
    }

    private JsonNode ConvertJsonArray(JsonArray source, int depth, string path)
      => ConvertItems(source, source.Count, depth, path);

    private JsonNode? ConvertJsonValue(JsonValue value, int depth, string path)
    {
      if (value.TryGetValue<JsonElement>(out var element))
      {
        return ConvertJsonElement(element, depth, path);
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
      if (value.TryGetValue<float>(out var floatValue)) return CreateFiniteNumber(floatValue, path);
      if (value.TryGetValue<double>(out var doubleValue)) return CreateFiniteNumber(doubleValue, path);
      if (value.TryGetValue<decimal>(out var decimalValue)) return JsonValue.Create(decimalValue)!;
      if (value.TryGetValue<Guid>(out var guidValue))
      {
        return JsonValue.Create(guidValue.ToString("D", CultureInfo.InvariantCulture))!;
      }

      throw SerializationError("JSON values must contain only supported JSON primitives.");
    }

    private JsonNode ConvertJsonElement(JsonElement element, int depth, string path)
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
            AddProperty(result, property.Name, Convert(property.Value, depth + 1, PropertyPath(path, property.Name)));
          }
          return result;
        }
        case JsonValueKind.Array:
          return ConvertItems(element.EnumerateArray().Select(item => (object)item), element.GetArrayLength(), depth, path);
        default:
          throw SerializationError(
            $"JSON value kind '{element.ValueKind}' is not supported."
          );
      }
    }

    private JsonNode ConvertDictionary(IDictionary source, int depth, string path)
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
          AddProperty(result, key, Convert(entry.Value, depth + 1, PropertyPath(path, key)));
        }
        return result;
      });
    }

    private JsonNode ConvertList(IList source, int depth, string path)
      => TrackReference(source, () => ConvertItems(source, source.Count, depth, path));

    /// <summary>
    /// Any other sequence, such as a LINQ query without ToList(). Enumeration
    /// runs the caller's query here, inside the open transaction, and stops
    /// after one item past the limit, so an endless sequence still ends.
    /// </summary>
    private JsonNode ConvertSequence(IEnumerable source, int depth, string path)
      => TrackReference(source, () => ConvertItems(source, total: null, depth, path));

    /// <summary>Tuples keep their element order; their names do not exist at run time.</summary>
    private JsonNode ConvertTuple(ITuple source, int depth, string path)
      => TrackReference(source, () =>
        ConvertItems(Enumerable.Range(0, source.Length).Select(index => source[index]), source.Length, depth, path));

    private JsonNode ConvertItems(IEnumerable source, int? total, int depth, string path)
    {
      var result = new JsonArray();
      var enumerator = source.GetEnumerator();
      try
      {
        while (true)
        {
          bool hasItem;
          object? item;
          try
          {
            hasItem = enumerator.MoveNext();
            item = hasItem ? enumerator.Current : null;
          }
          catch (Exception ex) when (ex is not (JsonRpcDispatchException or OperationCanceledException))
          {
            throw SerializationError(
              $"Enumerating the result at {path} failed: {ex.GetType().FullName}: {ex.Message}"
            );
          }
          if (!hasItem) break;

          if (result.Count == MaxCollectionItems)
          {
            Truncations.Add(new JsonObject
            {
              ["path"] = path,
              ["returned"] = MaxCollectionItems,
              ["total"] = total,
            });
            break;
          }
          if (_totalNodes >= MaxTotalNodes)
          {
            // The result as a whole is full: this list, and every list around
            // it, ends here. Items already started are completed.
            Truncations.Add(new JsonObject
            {
              ["path"] = path,
              ["returned"] = result.Count,
              ["total"] = total,
              ["reason"] = "size",
            });
            break;
          }
          result.Add(Convert(item, depth + 1, $"{path}[{result.Count}]"));
        }
      }
      finally
      {
        (enumerator as IDisposable)?.Dispose();
      }
      return result;
    }

    /// <summary>
    /// Anonymous objects, and classes or records the script itself declares:
    /// their public instance properties and fields, in declaration order.
    /// </summary>
    private JsonNode ConvertObjectMembers(object source, int depth, string path)
    {
      return TrackReference(source, () =>
      {
        var type = source.GetType();
        var properties = type
          .GetProperties(BindingFlags.Instance | BindingFlags.Public)
          .Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod != null)
          .OrderBy(property => property.MetadataToken)
          .Select(property => (property.Name, Read: (Func<object?>)(() => property.GetValue(source))));
        var fields = type
          .GetFields(BindingFlags.Instance | BindingFlags.Public)
          .OrderBy(field => field.MetadataToken)
          .Select(field => (field.Name, Read: (Func<object?>)(() => field.GetValue(source))));
        var members = properties.Concat(fields).ToArray();
        EnsureCollectionSize(members.Length);

        var result = new JsonObject();
        foreach (var (name, read) in members)
        {
          object? memberValue;
          try
          {
            memberValue = read();
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
            throw SerializationError(
              $"Result property '{name}' could not be read safely."
            );
          }

          var jsonName = JsonNamingPolicy.CamelCase.ConvertName(name);
          AddProperty(result, jsonName, Convert(memberValue, depth + 1, PropertyPath(path, jsonName)));
        }
        return result;
      });
    }

    private static string PropertyPath(string path, string name) => $"{path}.{name}";

    /// <summary>
    /// The name, as written in code: "Tangent" says more than 1 and saves the
    /// second run that a number without its name used to cost. Combined flags
    /// read "A, B"; a value without a name stays its number, as text.
    /// </summary>
    private static JsonNode ConvertEnum(Enum value)
      => JsonValue.Create(value.ToString())!;

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

    private JsonNode ConvertXy(double x, double y, string path)
      => new JsonObject
      {
        ["x"] = CreateFiniteNumber(x, PropertyPath(path, "x")),
        ["y"] = CreateFiniteNumber(y, PropertyPath(path, "y")),
      };

    private JsonNode ConvertXyz(double x, double y, double z, string path)
      => new JsonObject
      {
        ["x"] = CreateFiniteNumber(x, PropertyPath(path, "x")),
        ["y"] = CreateFiniteNumber(y, PropertyPath(path, "y")),
        ["z"] = CreateFiniteNumber(z, PropertyPath(path, "z")),
      };

    private JsonNode? CreateFiniteNumber(float value, string path)
      => float.IsFinite(value) ? JsonValue.Create(value) : RecordNonFinite(path);

    private JsonNode? CreateFiniteNumber(double value, string path)
      => double.IsFinite(value) ? JsonValue.Create(RoundNumber(value)) : RecordNonFinite(path);

    private JsonNode? RecordNonFinite(string path)
    {
      NonFiniteCount++;
      if (NonFinitePaths.Count < MaxNonFinitePaths) NonFinitePaths.Add(path);
      return null;
    }

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

    // Roslyn loads each compiled script as an in-memory assembly named
    // "ℛ*<guid>#<submission>-<n>"; only types declared by the caller live there.
    private static bool IsScriptDefinedType(Type type)
      => type.Assembly.GetName().Name?.StartsWith("ℛ*", StringComparison.Ordinal) == true;

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
      if (_totalNodes > MaxTotalNodesHard)
      {
        throw SerializationError(
          $"Result exceeded the maximum object graph size of {MaxTotalNodesHard} nodes."
        );
      }
    }
  }

  /// <summary>
  /// Shortens a converted result that is longer than
  /// <see cref="MaxResultCharacters"/>. The largest list loses items from its
  /// end first, then the longest text its tail, and only when neither is left
  /// the object with the most properties its last ones. Every cut is added to
  /// the truncation list, so the reader knows what is missing and can ask for
  /// the rest with a narrower script.
  /// </summary>
  private static class ResultSizeBudget
  {
    private sealed record Candidate(
      JsonNode Node, string Path, int Size, JsonNode? Parent, string? Key, int Index);

    public static JsonNode? Fit(JsonNode? result, List<JsonNode> truncations)
    {
      if (result is null) return null;
      var length = result.ToJsonString().Length;
      // Each round removes at least one item, property or character.
      for (var round = 0; length > MaxResultCharacters && round < 100_000; round++)
      {
        var candidates = new List<Candidate>();
        Measure(result, "$", parent: null, key: null, index: -1, candidates);
        var target = Choose(candidates);
        if (target is null) break;

        var excess = length - MaxResultCharacters;
        switch (target.Node)
        {
          case JsonArray array:
            CutArray(array, target.Path, excess, truncations);
            break;
          case JsonObject properties:
            CutObject(properties, target.Path, excess, truncations);
            break;
          default:
            var shortened = CutText(target, excess, truncations);
            if (target.Parent is JsonObject parentObject) parentObject[target.Key!] = shortened;
            else if (target.Parent is JsonArray parentArray) parentArray[target.Index] = shortened;
            else result = shortened;
            break;
        }
        length = result.ToJsonString().Length;
      }

      if (length > MaxResultCharacters)
      {
        throw SerializationError(
          $"The result is {length} characters long, over the limit of {MaxResultCharacters}, " +
          "and has no list, text or object left to shorten. Return fewer values."
        );
      }
      return result;
    }

    /// <summary>
    /// Lists first, because the end of a list is the least surprising thing to
    /// lose; then texts; an object is cut only when nothing else is left, and
    /// then the one with the most properties, which is a table keyed by name
    /// rather than a record.
    /// </summary>
    private static Candidate? Choose(List<Candidate> candidates)
    {
      var lists = candidates.Where(candidate => candidate.Node is JsonArray).ToList();
      if (lists.Count > 0) return lists.MaxBy(candidate => candidate.Size);
      var texts = candidates.Where(candidate => candidate.Node is JsonValue).ToList();
      if (texts.Count > 0) return texts.MaxBy(candidate => candidate.Size);
      return candidates
        .OrderByDescending(candidate => ((JsonObject)candidate.Node).Count)
        .ThenByDescending(candidate => candidate.Size)
        .FirstOrDefault();
    }

    /// <summary>Approximate JSON length of a node; collects what could be cut.</summary>
    private static int Measure(
      JsonNode? node, string path, JsonNode? parent, string? key, int index, List<Candidate> candidates)
    {
      switch (node)
      {
        case null:
          return 4;
        case JsonArray array:
        {
          var size = 2 + Math.Max(0, array.Count - 1);
          for (var position = 0; position < array.Count; position++)
          {
            size += Measure(array[position], $"{path}[{position}]", array, null, position, candidates);
          }
          if (array.Count > 1) candidates.Add(new Candidate(array, path, size, parent, key, index));
          return size;
        }
        case JsonObject properties:
        {
          var size = 2 + Math.Max(0, properties.Count - 1);
          foreach (var property in properties)
          {
            size += property.Key.Length + 3
              + Measure(property.Value, $"{path}.{property.Key}", properties, property.Key, -1, candidates);
          }
          if (properties.Count > 1) candidates.Add(new Candidate(properties, path, size, parent, key, index));
          return size;
        }
        default:
        {
          var size = node.ToJsonString().Length;
          if (node is JsonValue value && value.TryGetValue<string>(out var text)
            && text.Length > MinKeptTextCharacters)
          {
            candidates.Add(new Candidate(node, path, size, parent, key, index));
          }
          return size;
        }
      }
    }

    private static void CutArray(JsonArray array, string path, int excess, List<JsonNode> truncations)
    {
      var total = array.Count;
      var keep = total;
      var removed = 0;
      while (keep > 1 && removed < excess)
      {
        keep--;
        removed += (array[keep]?.ToJsonString().Length ?? 4) + 1;
      }
      for (var position = total - 1; position >= keep; position--) array.RemoveAt(position);
      RecordCut(truncations, path, keep, total, "returned", "total");
      DropEntriesInside(truncations, path, keep);
    }

    private static void CutObject(JsonObject properties, string path, int excess, List<JsonNode> truncations)
    {
      var names = properties.Select(property => property.Key).ToList();
      var keep = names.Count;
      var removed = 0;
      while (keep > 1 && removed < excess)
      {
        keep--;
        removed += names[keep].Length + 4 + (properties[names[keep]]?.ToJsonString().Length ?? 4);
      }
      foreach (var name in names.Skip(keep))
      {
        properties.Remove(name);
        truncations.RemoveAll(entry => IsAtOrBelow(PathOf(entry), $"{path}.{name}"));
      }
      RecordCut(truncations, path, keep, names.Count, "returned", "total");
    }

    private static JsonNode CutText(Candidate target, int excess, List<JsonNode> truncations)
    {
      var text = target.Node.GetValue<string>();
      // Escaped characters make the JSON text longer than the string itself.
      var share = Math.Max(0, target.Size - excess) / (double)target.Size;
      var keep = Math.Clamp((int)(text.Length * share), MinKeptTextCharacters, text.Length - 1);
      if (char.IsHighSurrogate(text[keep - 1])) keep--;
      RecordCut(truncations, target.Path, keep, text.Length, "returnedCharacters", "totalCharacters");
      return JsonValue.Create(text[..keep])!;
    }

    /// <summary>
    /// One entry per path. A list that was already cut to the item limit keeps
    /// the total recorded then, which is the real one.
    /// </summary>
    private static void RecordCut(
      List<JsonNode> truncations, string path, int returned, int total, string returnedName, string totalName)
    {
      var existing = truncations.OfType<JsonObject>().FirstOrDefault(entry => PathOf(entry) == path);
      if (existing is null)
      {
        truncations.Add(new JsonObject
        {
          ["path"] = path,
          [returnedName] = returned,
          [totalName] = total,
          ["reason"] = "size",
        });
        return;
      }
      existing[returnedName] = returned;
      existing["reason"] = "size";
    }

    /// <summary>Entries about lists inside items that are no longer returned.</summary>
    private static void DropEntriesInside(List<JsonNode> truncations, string path, int keep)
    {
      truncations.RemoveAll(entry =>
      {
        var entryPath = PathOf(entry);
        if (entryPath is null || !entryPath.StartsWith(path + "[", StringComparison.Ordinal)) return false;
        var close = entryPath.IndexOf(']', path.Length + 1);
        return close > 0
          && int.TryParse(entryPath.AsSpan(path.Length + 1, close - path.Length - 1), NumberStyles.None,
            CultureInfo.InvariantCulture, out var position)
          && position >= keep;
      });
    }

    private static bool IsAtOrBelow(string? entryPath, string path)
      => entryPath is not null
        && (entryPath == path
          || entryPath.StartsWith(path + ".", StringComparison.Ordinal)
          || entryPath.StartsWith(path + "[", StringComparison.Ordinal));

    private static string? PathOf(JsonNode entry)
      => entry is JsonObject fields && fields["path"] is JsonValue value && value.TryGetValue<string>(out var path)
        ? path
        : null;
  }
}
