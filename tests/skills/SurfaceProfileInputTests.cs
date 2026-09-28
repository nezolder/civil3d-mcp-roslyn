using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Execute only the recipe's validation prefix. These stand-ins are not Civil 3D mocks.
internal static class SurfaceProfileInputTests
{
  internal static void Run(string repositoryRoot, IEnumerable<MetadataReference> platformReferences)
  {
    var markdown = File.ReadAllText(Path.Combine(repositoryRoot, "skills", "profiles", "create_surface_profile_view.skill.md"));
    var code = Regex.Match(markdown, "```csharp\\s*\\r?\\n([\\s\\S]*?)\\r?\\n```").Groups[1].Value;
    const string marker = "// Host access starts here.";
    var markerIndex = code.IndexOf(marker, StringComparison.Ordinal);
    if (markerIndex < 0) throw new InvalidOperationException("Missing explicit host-access boundary in surface-profile skill.");
    var prefix = code[..markerIndex];
    var source = """
      using System;
      using System.Linq;
      public static class InputProbe
      {
        public static object Run()
        {
      """ + prefix + "\nreturn new { success = true };\n}}";

    var configured = new Dictionary<string, string>
    {
      ["alignmentHandle"] = "\"AB12\"",
      ["surfaceHandle"] = "\"CD34\"",
      ["profileName"] = "\"EG-01\"",
      ["profileViewName"] = "\"EG-01 view\"",
      ["profileLabelSetName"] = "\"Ground labels\"",
      ["bandSetStyleName"] = "\"Road bands\"",
    };
    var cases = new (string Name, bool Expected, Dictionary<string, string> Values)[]
    {
      ("unchanged placeholders refuse authoring", false, new()),
      ("configured input reaches host boundary", true, new(configured)),
      ("invalid alignment handle refuses authoring", false, Change("alignmentHandle", "\"not-a-handle\"")),
      ("invalid surface handle refuses authoring", false, Change("surfaceHandle", "\"-1\"")),
      ("blank profile name refuses authoring", false, Change("profileName", "\" \"")),
      ("placeholder label set refuses authoring", false, Change("profileLabelSetName", "\"PROFILE_LABEL_SET_NAME\"")),
      ("placeholder band set refuses authoring", false, Change("bandSetStyleName", "\"PROFILE_VIEW_BAND_SET_STYLE_NAME\"")),
      ("nonfinite insertion X refuses authoring", false, Change("insertionX", "double.NaN")),
      ("nonfinite insertion Y refuses authoring", false, Change("insertionY", "double.NegativeInfinity")),
      ("untrimmed style name refuses authoring", false, Change("profileStyleName", "\" Terep\"")),
      ("control character in view name refuses authoring", false, Change("profileViewName", "\"bad\\nview\"")),
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
        syntax = syntax.ReplaceNode(original, SyntaxFactory.ParseExpression(replacement));
      }
      var compilation = CSharpCompilation.Create("SurfaceProfileInputProbe" + index,
        new[] { CSharpSyntaxTree.Create((CompilationUnitSyntax)syntax) }, platformReferences,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      using var stream = new MemoryStream();
      var emitted = compilation.Emit(stream);
      if (!emitted.Success)
        throw new InvalidOperationException(test.Name + ": " + string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
      stream.Position = 0;
      var loadContext = new AssemblyLoadContext("surface-profile-input-" + index, isCollectible: true);
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
    Console.WriteLine($"Surface-profile skill input validation passed {cases.Length} host-free cases (native Civil behavior not tested).");

    Dictionary<string, string> Change(string name, string expression)
    {
      var values = new Dictionary<string, string>(configured) { [name] = expression };
      return values;
    }
  }
}
