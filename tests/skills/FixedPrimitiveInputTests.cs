using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Execute only the skill's input-validation prefix. These small coordinate value
// stand-ins are NOT Civil mocks and cannot prove native construction or rollback.
internal static class FixedPrimitiveInputTests
{
  internal static void Run(string repositoryRoot, IEnumerable<MetadataReference> platformReferences)
  {
    var markdown = File.ReadAllText(Path.Combine(repositoryRoot, "skills", "alignments", "replace_alignment_with_fixed_primitives.skill.md"));
    var code = Regex.Match(markdown, "```csharp\\s*\\r?\\n([\\s\\S]*?)\\r?\\n```").Groups[1].Value;
    const string marker = "// Host access starts here.";
    var markerIndex = code.IndexOf(marker, StringComparison.Ordinal);
    if (markerIndex < 0) throw new InvalidOperationException("Missing explicit host-access boundary in fixed-primitive skill.");
    var prefix = code[..markerIndex];
    var source = """
      using System;
      using System.Linq;
      using System.Collections.Generic;
      public readonly record struct Point2d(double X, double Y)
      {
        public double GetDistanceTo(Point2d p) => Math.Sqrt((X-p.X)*(X-p.X)+(Y-p.Y)*(Y-p.Y));
      }
      public readonly record struct Point3d(double X, double Y, double Z);
      public enum SpiralCurveType { InCurve, OutCurve }
      public static class InputProbe
      {
        public static object Run()
        {
      """ + prefix + "\nreturn new { success = true };\n}}";

    var configured = new Dictionary<string, string>
    {
      ["alignmentHandle"] = "\"AB12\"",
      ["auditVerifiedPlainIndependentCentreline"] = "true",
      ["auditVerifiedNoStationEquations"] = "true",
      ["auditVerifiedNoSuperelevation"] = "true",
    };
    var reverseChain = new Dictionary<string, string>(configured);
    var fixture = File.ReadAllText(Path.Combine(repositoryRoot, "tests", "skills", "fixtures", "fixed-primitive-reverse-inputs.csharp"));
    foreach (var variable in CSharpSyntaxTree.ParseText(fixture).GetRoot()
      .DescendantNodes().OfType<VariableDeclaratorSyntax>())
      reverseChain[variable.Identifier.ValueText] = variable.Initializer!.Value.ToString();

    var cases = new (string Name, bool Expected, Dictionary<string, string> Values)[]
    {
      ("unchanged placeholders refuse authoring", false, new()),
      ("configured simple input reaches host boundary", true, new(configured)),
      ("unknown superelevation refuses authoring", false, Change("auditVerifiedNoSuperelevation", "false")),
      ("invalid handle refuses authoring", false, Change("alignmentHandle", "\"not-a-handle\"")),
      ("NaN tolerance refuses authoring", false, Change("tolerance", "double.NaN")),
      ("infinite angular tolerance refuses authoring", false, Change("directionTolerance", "double.PositiveInfinity")),
      ("NaN starting station refuses authoring", false, Change("expectedStartingStation", "double.NaN")),
      ("nonzero start is not hidden by coordinate tolerance", false, Change("expectedStartingStation", "0.0005")),
      ("infinite endpoint refuses authoring", false, Change("expectedEnd", "new Point2d(double.PositiveInfinity, 0)")),
      ("empty plan refuses authoring", false, Change("plannedPrimitives", "($original).Take(0).ToArray()")),
      ("oversized plan refuses authoring", false, Change("plannedPrimitives", "Enumerable.Repeat(($original)[0], 65).ToArray()")),
      ("disconnected plan refuses authoring", false, Change("plannedPrimitives", "Enumerable.Repeat(($original)[0], 2).ToArray()")),
      ("missing baseline refuses authoring", false, Change("expectedExistingPrimitives", "($original).Take(0).ToArray()")),
      ("bounded speed records refuse excess input", false, Change("expectedSpeedRecords", "Enumerable.Repeat(($original)[0], 33).ToArray()")),
      ("speed station outside replacement refuses authoring", false, Change("expectedSpeedRecords", "new[] { new { station = 2.0, value = 80.0, comment = \"outside\" } }")),
      ("synthetic reverse clothoid chain reaches host boundary", true, new(reverseChain)),
      ("reverse chain with inconsistent join direction refuses authoring", false, new(reverseChain)
      {
        ["plannedPrimitives"] = reverseChain["plannedPrimitives"].Replace("startDirection = 0.8726646259971649", "startDirection = 0.8"),
      }),
      ("negative infinity is not a tangent radius", false, new(reverseChain)
      {
        ["plannedPrimitives"] = reverseChain["plannedPrimitives"].Replace("radiusIn = double.PositiveInfinity", "radiusIn = double.NegativeInfinity"),
      }),
    };

    for (var index = 0; index < cases.Length; index++)
    {
      var test = cases[index];
      var syntax = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)).GetRoot();
      foreach (var (name, replacement) in test.Values)
      {
        var variable = syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>()
          .Single(node => node.Identifier.ValueText == name);
        var original = variable.Initializer?.Value ?? throw new InvalidOperationException("Missing initializer: " + name);
        syntax = syntax.ReplaceNode(original, SyntaxFactory.ParseExpression(replacement.Replace("$original", original.ToString())));
      }
      var compilation = CSharpCompilation.Create("FixedPrimitiveInputProbe" + index,
        new[] { CSharpSyntaxTree.Create((CompilationUnitSyntax)syntax) }, platformReferences,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      using var stream = new MemoryStream();
      var emitted = compilation.Emit(stream);
      if (!emitted.Success)
        throw new InvalidOperationException(test.Name + ": " + string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
      stream.Position = 0;
      var loadContext = new AssemblyLoadContext("fixed-input-" + index, isCollectible: true);
      try
      {
        var result = loadContext.LoadFromStream(stream).GetType("InputProbe")!
          .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        var succeeded = (bool)result.GetType().GetProperty("success")!.GetValue(result)!;
        if (succeeded != test.Expected)
          throw new InvalidOperationException(test.Name + ": expected success=" + test.Expected + ", actual=" + succeeded);
      }
      finally { loadContext.Unload(); }
    }
    Console.WriteLine($"Fixed-primitive skill input validation passed {cases.Length} host-free cases (native Civil behavior not tested).");

    Dictionary<string, string> Change(string name, string expression)
    {
      var values = new Dictionary<string, string>(configured) { [name] = expression };
      return values;
    }
  }
}
