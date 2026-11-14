using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// The lookup returns one short page, so its order decides whether the type
/// that was asked for is on it. Alphabetical order used to put a helper type
/// first and the exact one out of sight.
/// </summary>
internal static class ApiLookupRankingTests
{
  public static void RunAll()
  {
    var tests = new (string Name, Action Run)[]
    {
      ("api lookup puts the exactly named type first", ExactTypeFirst),
      ("api lookup puts the exactly named member of the named type first", ExactMemberFirst),
      ("api lookup keeps one fixed order among equal matches", EqualMatchesKeepOrder),
      ("api lookup also reads the pressure-pipe and data-shortcut assemblies", ExtraAssemblies),
    };

    var main = Fixture("AeccDbMgd",
      ("Fixture.Api.AAAlignmentTools", new[] { "Zeta" }),
      ("Fixture.Api.Alignment", new[] { "Length", "StationOffset", "Name" }),
      ("Fixture.Api.AlignmentArc", new[] { "Length", "Radius" }),
      ("Fixture.Api.Profile", new[] { "AlignmentId", "LengthUnits" }));
    var pressure = Fixture("AeccPressurePipesMgd", ("Fixture.Pressure.PressurePipe", new[] { "Length" }));
    var shortcuts = Fixture("AeccDataShortcutMgd", ("Fixture.Shortcuts.DataShortcutManager", new[] { "Count" }));
    var other = Fixture("NotAllowlisted", ("Fixture.Other.Alignment", new[] { "Length" }));
    ApiLookup.LoadedAssembliesProvider = () => new[] { main, pressure, shortcuts, other };
    try
    {
      foreach (var (name, run) in tests)
      {
        run();
        Console.WriteLine($"PASS {name}");
      }
    }
    finally
    {
      ApiLookup.LoadedAssembliesProvider = null;
    }
  }

  private static void ExactTypeFirst()
  {
    var results = Lookup("alignment", limit: 4);
    AssertSequence(results, new[]
    {
      "Fixture.Api.Alignment",
      // Its constructor carries the same name.
      "Fixture.Api.Alignment|Alignment()",
      // Then the types that start with the word, before any member.
      "Fixture.Api.AlignmentArc",
      "Fixture.Api.AlignmentArc|AlignmentArc()",
    });
    Assert(Lookup("Alignment", limit: 1).Single() == "Fixture.Api.Alignment",
      "a page of one must hold the exactly named type, not the first in the alphabet");
  }

  private static void ExactMemberFirst()
  {
    // Every word must still occur; among those, the member of the exactly named type leads.
    AssertSequence(Lookup("Length Alignment", limit: 3), new[]
    {
      "Fixture.Api.Alignment|double Length { get; }",
      "Fixture.Api.AlignmentArc|double Length { get; }",
    });
    Assert(Lookup("Alignment Length", limit: 1).Single() == "Fixture.Api.Alignment|double Length { get; }",
      "word order must not matter");
    // A member name alone: the members called exactly so, then those that start with it.
    AssertSequence(Lookup("Length", limit: 5), new[]
    {
      "Fixture.Api.Alignment|double Length { get; }",
      "Fixture.Api.AlignmentArc|double Length { get; }",
      "Fixture.Pressure.PressurePipe|double Length { get; }",
      "Fixture.Api.Profile|double LengthUnits { get; }",
    });
  }

  private static void EqualMatchesKeepOrder()
  {
    var first = Lookup("Fixture", limit: 50);
    Assert(first.SequenceEqual(Lookup("Fixture", limit: 50)), "the same query must return the same order");
    var types = first.Where(item => !item.Contains('|')).ToArray();
    Assert(types.SequenceEqual(new[]
    {
      "Fixture.Shortcuts.DataShortcutManager", "Fixture.Api.AAAlignmentTools", "Fixture.Api.Alignment",
      "Fixture.Api.AlignmentArc", "Fixture.Api.Profile", "Fixture.Pressure.PressurePipe",
    }), $"equal matches stay sorted by assembly, then type: {string.Join(", ", types)}");
  }

  private static void ExtraAssemblies()
  {
    Assert(Lookup("PressurePipe", limit: 1).Single() == "Fixture.Pressure.PressurePipe", "pressure pipes must be found");
    Assert(Lookup("DataShortcutManager", limit: 1).Single() == "Fixture.Shortcuts.DataShortcutManager",
      "data shortcuts must be found");
    Assert(Lookup("Fixture.Other", limit: 5).Length == 0, "an assembly outside the list stays unread");
  }

  private static string[] Lookup(string query, int limit)
  {
    var response = JsonSerializer.SerializeToNode(
      ApiLookup.Lookup(new JsonObject { ["query"] = query, ["limit"] = limit }),
      new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
    return response["results"]!.AsArray()
      .Select(item => item!["member"] is { } member
        ? $"{item["type"]!.GetValue<string>()}|{member.GetValue<string>()}"
        : item["type"]!.GetValue<string>())
      .ToArray();
  }

  /// <summary>An in-memory assembly with public types that each have read-only double properties.</summary>
  private static Assembly Fixture(string assemblyName, params (string Type, string[] Properties)[] types)
  {
    var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
    var module = assembly.DefineDynamicModule(assemblyName);
    foreach (var (typeName, properties) in types)
    {
      var type = module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class);
      type.DefineDefaultConstructor(MethodAttributes.Public);
      foreach (var propertyName in properties)
      {
        var getter = type.DefineMethod($"get_{propertyName}",
          MethodAttributes.Public | MethodAttributes.SpecialName, typeof(double), Type.EmptyTypes);
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Ret);
        type.DefineProperty(propertyName, PropertyAttributes.None, typeof(double), Type.EmptyTypes)
          .SetGetMethod(getter);
      }
      _ = type.CreateType();
    }
    return assembly;
  }

  private static void AssertSequence(string[] actual, string[] expected)
    => Assert(actual.SequenceEqual(expected),
      $"expected [{string.Join("; ", expected)}] but got [{string.Join("; ", actual)}]");

  private static void Assert(bool condition, string message)
  {
    if (!condition) throw new InvalidOperationException(message);
  }
}
