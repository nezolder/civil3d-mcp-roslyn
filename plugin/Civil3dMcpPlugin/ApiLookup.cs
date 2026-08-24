using System.Reflection;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// Reads a bounded snapshot of public metadata from Civil 3D host assemblies that
/// are already loaded. This deliberately does not load assemblies or touch a drawing.
/// </summary>
internal static class ApiLookup
{
  private const int DefaultLimit = 20;
  private const int MaximumQueryLength = 200;
  private const int MaximumLimit = 50;

  private static readonly HashSet<string> AllowedAssemblies = new(StringComparer.OrdinalIgnoreCase)
  {
    "accoremgd",
    "Acdbmgd",
    "Acmgd",
    "AecBaseMgd",
    "AeccDbMgd",
  };

  // Test-only seam. Production always snapshots AppDomain.CurrentDomain.
  internal static Func<IReadOnlyList<Assembly>>? LoadedAssembliesProvider { get; set; }

  public static object Lookup(JsonObject? parameters)
  {
    var request = ParseRequest(parameters);
    if (request.Assembly != null && !AllowedAssemblies.Contains(request.Assembly))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Parameter 'assembly' must name an allowlisted loaded host assembly."
      );
    }

    var assemblies = SnapshotAllowedAssemblies();
    if (assemblies.Count == 0)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_LOOKUP_UNAVAILABLE",
        "Civil 3D API metadata is unavailable because no allowlisted host assemblies are loaded."
      );
    }

    var matchingAssemblies = request.Assembly == null
      ? assemblies
      : assemblies.Where(assembly => AssemblyNameEquals(assembly, request.Assembly)).ToArray();

    if (request.Assembly != null && matchingAssemblies.Count == 0)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.API_LOOKUP_UNAVAILABLE",
        "The requested allowlisted host assembly is not loaded."
      );
    }

    var results = new List<ApiLookupItem>();
    foreach (var assembly in matchingAssemblies)
    {
      var assemblyName = assembly.GetName().Name!;
      foreach (var type in GetPublicTypes(assembly))
      {
        var typeName = FormatType(type);
        var typeNamespace = type.Namespace ?? string.Empty;
        if (request.Namespace != null && !NamespaceMatches(typeNamespace, request.Namespace)) continue;

        if (MatchesAllTokens(typeName, request.QueryTokens))
        {
          results.Add(new ApiLookupItem(assemblyName, typeName, null));
        }

        foreach (var member in GetPublicDeclaredMembers(type))
        {
          string? signature;
          try
          {
            signature = FormatMember(member);
          }
          catch
          {
            // Some host metadata can be only partially resolvable; skip that member.
            continue;
          }
          if (signature == null) continue;
          if (MatchesAllTokens($"{typeName} {signature}", request.QueryTokens))
          {
            results.Add(new ApiLookupItem(assemblyName, typeName, signature));
          }
        }
      }
    }

    var ordered = results
      .OrderBy(item => item.Assembly, ApiStringComparer.Instance)
      .ThenBy(item => item.Type, ApiStringComparer.Instance)
      .ThenBy(item => item.Member ?? string.Empty, ApiStringComparer.Instance)
      .ToArray();

    var returned = ordered.Take(request.Limit).ToArray();
    return new ApiLookupResponse(ordered.Length, returned.Length, ordered.Length > returned.Length, returned);
  }

  private static IReadOnlyList<Assembly> SnapshotAllowedAssemblies()
  {
    var snapshot = LoadedAssembliesProvider?.Invoke() ?? AppDomain.CurrentDomain.GetAssemblies();
    return snapshot
      .Where(assembly =>
      {
        try { return AllowedAssemblies.Contains(assembly.GetName().Name ?? string.Empty); }
        catch { return false; }
      })
      .OrderBy(assembly => assembly.GetName().Name, ApiStringComparer.Instance)
      .ToArray();
  }

  private static IEnumerable<Type> GetPublicTypes(Assembly assembly)
  {
    try
    {
      return assembly.GetTypes().Where(type => type.IsPublic || type.IsNestedPublic);
    }
    catch (ReflectionTypeLoadException exception)
    {
      // A partial metadata snapshot remains safe; loader diagnostics are not exposed.
      return exception.Types
        .Where(type => type != null && (type.IsPublic || type.IsNestedPublic))
        .Cast<Type>();
    }
    catch
    {
      return Array.Empty<Type>();
    }
  }

  private static IEnumerable<MemberInfo> GetPublicDeclaredMembers(Type type)
  {
    try
    {
      return type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Where(member => member switch
        {
          ConstructorInfo => true,
          MethodInfo method => !method.IsSpecialName,
          _ => member.MemberType is MemberTypes.Constructor or MemberTypes.Event or MemberTypes.Field or MemberTypes.Property,
        });
    }
    catch
    {
      return Array.Empty<MemberInfo>();
    }
  }

  private static ApiLookupRequest ParseRequest(JsonObject? parameters)
  {
    var query = GetRequiredTrimmedString(parameters, "query", MaximumQueryLength);
    var assembly = GetOptionalTrimmedString(parameters, "assembly", MaximumQueryLength);
    var @namespace = GetOptionalTrimmedString(parameters, "namespace", MaximumQueryLength);
    var limit = GetOptionalLimit(parameters);
    return new ApiLookupRequest(
      query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
      assembly,
      @namespace,
      limit
    );
  }

  private static string GetRequiredTrimmedString(JsonObject? parameters, string name, int maximumLength)
  {
    if (!TryGetString(parameters, name, out var value) || string.IsNullOrWhiteSpace(value))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' is required.");
    }
    var trimmed = value.Trim();
    if (trimmed.Length > maximumLength)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' must be at most {maximumLength} characters.");
    }
    return trimmed;
  }

  private static string? GetOptionalTrimmedString(JsonObject? parameters, string name, int maximumLength)
  {
    if (parameters?[name] == null) return null;
    if (!TryGetString(parameters, name, out var value) || string.IsNullOrWhiteSpace(value))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' must be a non-empty string when supplied.");
    }
    var trimmed = value.Trim();
    if (trimmed.Length > maximumLength)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' must be at most {maximumLength} characters.");
    }
    return trimmed;
  }

  private static int GetOptionalLimit(JsonObject? parameters)
  {
    if (parameters?["limit"] == null) return DefaultLimit;
    if (parameters["limit"] is not JsonValue value || !value.TryGetValue<int>(out var limit) || limit < 1 || limit > MaximumLimit)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter 'limit' must be an integer between 1 and {MaximumLimit}.");
    }
    return limit;
  }

  private static bool TryGetString(JsonObject? parameters, string name, out string value)
  {
    value = string.Empty;
    if (parameters?[name] is not JsonValue jsonValue ||
        !jsonValue.TryGetValue<string>(out var parsed) ||
        parsed == null)
    {
      return false;
    }
    value = parsed;
    return true;
  }

  private static bool AssemblyNameEquals(Assembly assembly, string name)
    => string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase);

  private static bool NamespaceMatches(string typeNamespace, string filter)
    => string.Equals(typeNamespace, filter, StringComparison.OrdinalIgnoreCase) ||
       typeNamespace.StartsWith($"{filter}.", StringComparison.OrdinalIgnoreCase);

  private static bool MatchesAllTokens(string candidate, IEnumerable<string> tokens)
    => tokens.All(token => candidate.Contains(token, StringComparison.OrdinalIgnoreCase));

  private static string FormatType(Type type) => type.FullName ?? type.Name;

  private static string? FormatMember(MemberInfo member)
    => member switch
    {
      MethodInfo method => $"{FormatStatic(method.IsStatic)}{FormatParameterType(method.ReturnType)} {method.Name}{FormatMethodTypeParameters(method)}({FormatParameters(method.GetParameters())})",
      ConstructorInfo constructor => $"{constructor.DeclaringType?.Name ?? ".ctor"}({FormatParameters(constructor.GetParameters())})",
      PropertyInfo property => property.GetIndexParameters().Length == 0
        ? $"{FormatStatic(IsStatic(property))}{FormatParameterType(property.PropertyType)} {property.Name}{FormatPropertyAccessors(property)}"
        : $"{FormatStatic(IsStatic(property))}{FormatParameterType(property.PropertyType)} {property.Name}[{FormatParameters(property.GetIndexParameters())}]{FormatPropertyAccessors(property)}",
      FieldInfo field => $"{FormatFieldModifiers(field)}{FormatParameterType(field.FieldType)} {field.Name}",
      EventInfo @event => $"{FormatStatic(IsStatic(@event))}event {FormatParameterType(@event.EventHandlerType!)} {@event.Name}",
      _ => null,
    };

  private static string FormatStatic(bool isStatic) => isStatic ? "static " : string.Empty;

  private static string FormatFieldModifiers(FieldInfo field)
  {
    if (field.IsLiteral) return "const ";
    return $"{FormatStatic(field.IsStatic)}{(field.IsInitOnly ? "readonly " : string.Empty)}";
  }

  private static bool IsStatic(PropertyInfo property)
    => (property.GetMethod ?? property.SetMethod)?.IsStatic == true;

  private static bool IsStatic(EventInfo @event)
    => (@event.AddMethod ?? @event.RemoveMethod)?.IsStatic == true;

  private static string FormatPropertyAccessors(PropertyInfo property)
  {
    var accessors = new List<string>(2);
    if (property.GetMethod?.IsPublic == true) accessors.Add("get;");
    if (property.SetMethod?.IsPublic == true) accessors.Add("set;");
    return accessors.Count == 0 ? string.Empty : $" {{ {string.Join(" ", accessors)} }}";
  }

  private static string FormatParameters(IEnumerable<ParameterInfo> parameters)
    => string.Join(", ", parameters.Select(FormatParameter));

  private static string FormatParameter(ParameterInfo parameter)
  {
    var modifier = parameter.IsOut
      ? "out "
      : parameter.ParameterType.IsByRef
        ? parameter.IsIn ? "in " : "ref "
        : string.Empty;
    return $"{modifier}{FormatParameterType(parameter.ParameterType)} {parameter.Name ?? "value"}";
  }

  private static string FormatMethodTypeParameters(MethodInfo method)
    => !method.IsGenericMethod
      ? string.Empty
      : $"<{string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name))}>";

  private static string FormatParameterType(Type type)
  {
    if (type.IsByRef) type = type.GetElementType()!;
    if (type.IsArray) return $"{FormatParameterType(type.GetElementType()!)}[]";
    if (CSharpAliases.TryGetValue(type, out var alias)) return alias;
    if (!type.IsGenericType) return type.Name;
    var name = type.Name;
    var tick = name.IndexOf('`');
    if (tick >= 0) name = name[..tick];
    return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FormatParameterType))}>";
  }

  private sealed record ApiLookupRequest(string[] QueryTokens, string? Assembly, string? Namespace, int Limit);
  private sealed record ApiLookupItem(string Assembly, string Type, string? Member);
  private sealed record ApiLookupResponse(int Total, int Returned, bool Truncated, IReadOnlyList<ApiLookupItem> Results);

  private static readonly IReadOnlyDictionary<Type, string> CSharpAliases = new Dictionary<Type, string>
  {
    [typeof(void)] = "void",
    [typeof(bool)] = "bool",
    [typeof(byte)] = "byte",
    [typeof(sbyte)] = "sbyte",
    [typeof(short)] = "short",
    [typeof(ushort)] = "ushort",
    [typeof(int)] = "int",
    [typeof(uint)] = "uint",
    [typeof(long)] = "long",
    [typeof(ulong)] = "ulong",
    [typeof(float)] = "float",
    [typeof(double)] = "double",
    [typeof(decimal)] = "decimal",
    [typeof(char)] = "char",
    [typeof(string)] = "string",
    [typeof(object)] = "object",
  };

  private sealed class ApiStringComparer : IComparer<string?>
  {
    public static readonly ApiStringComparer Instance = new();

    public int Compare(string? left, string? right)
    {
      var insensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right);
      return insensitive != 0 ? insensitive : StringComparer.Ordinal.Compare(left, right);
    }
  }
}
