using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Checks one code body the way the plugin compiles it (RoslynExecutor): as a
/// script with the same imports and default type aliases, so a body may start
/// with its own using lines and declare classes. Then the plugin's await rule
/// and script guard. Nothing is run. Returns the process exit code.
/// </summary>
internal static class SingleCodeCheck
{
  // Same list as RoslynExecutor.BuildOptions.
  private static readonly string[] Imports =
  {
    "System",
    "System.Linq",
    "System.Collections.Generic",
    "System.Text",
    "Autodesk.AutoCAD.ApplicationServices",
    "Autodesk.AutoCAD.DatabaseServices",
    "Autodesk.AutoCAD.EditorInput",
    "Autodesk.AutoCAD.Geometry",
    "Autodesk.AutoCAD.Runtime",
    "Autodesk.Civil",
    "Autodesk.Civil.ApplicationServices",
    "Autodesk.Civil.DatabaseServices",
    "Autodesk.Civil.Settings",
  };

  // Same list as RoslynExecutor.DefaultTypeAliases.
  private static readonly (string Name, string Target)[] DefaultTypeAliases =
  {
    ("DBObject", "Autodesk.AutoCAD.DatabaseServices.DBObject"),
    ("Entity", "Autodesk.AutoCAD.DatabaseServices.Entity"),
    ("ErrorStatus", "Autodesk.AutoCAD.Runtime.ErrorStatus"),
    ("Exception", "System.Exception"),
    ("Section", "Autodesk.Civil.DatabaseServices.Section"),
    ("Surface", "Autodesk.Civil.DatabaseServices.Surface"),
  };

  // Stand-ins for the plugin's script globals (ScriptContext). A later script
  // submission sees them like the globals the plugin passes.
  private const string GlobalsPrelude = """
    Document Document = null;
    CivilDocument CivilDoc = null;
    Database Database = null;
    Transaction Transaction = null;
    Editor Editor = null;
    object ImageFile(string path) => path;
    string NewImagePath(string extension = "png") => extension;
    """;

  private static readonly CSharpParseOptions ScriptParse =
    new(LanguageVersion.Latest, kind: SourceCodeKind.Script);

  public static int Run(string code, string path, IReadOnlyList<MetadataReference> references)
  {
    var exitCode = Check(code, path, references, out var message);
    if (exitCode == 0) Console.WriteLine(message);
    else Console.Error.WriteLine(message);
    return exitCode;
  }

  /// <summary>
  /// Cases the check must keep telling apart; run with the catalog checks, so
  /// that a change of the stand-in is noticed without Civil 3D.
  /// </summary>
  public static void SelfTest(IReadOnlyList<MetadataReference> references)
  {
    var cases = new (string Name, string Code, int Expected, string Expect)[]
    {
      ("plain body", "return Database.Filename;", 0, "passed"),
      ("own using line and record",
        "using Autodesk.Civil.DatabaseServices.Styles;\nrecord Row(string Name);\nSurfaceStyle style = null;\nreturn new Row(\"a\");",
        0, "passed"),
      ("default names", "Surface s = null; Section x = null; Entity e = null; Exception ex = null;\nreturn s == null;", 0, "passed"),
      ("picture helpers", "return ImageFile(NewImagePath());", 0, "passed"),
      ("top-level using var", "using var tr = Database.TransactionManager.StartTransaction();\nreturn 1;", 1, "error"),
      ("await", "await System.Threading.Tasks.Task.Delay(1);\nreturn 1;", 1, "MCP0001"),
      ("script guard", "System.IO.File.Delete(\"x\");\nreturn 1;", 1, "script guard"),
      ("no enumerator", "var s = (Surface)null;\nforeach (var op in s.Operations) { }\nreturn 1;", 1, "CS1579"),
    };
    foreach (var c in cases)
    {
      var exitCode = Check(c.Code, $"selftest-{c.Name}.cs", references, out var message);
      if (exitCode != c.Expected || !message.Contains(c.Expect, StringComparison.Ordinal))
        throw new InvalidOperationException(
          $"Single code-body check, case '{c.Name}': expected exit {c.Expected} with '{c.Expect}', got {exitCode}: {message}");
    }
    Console.WriteLine($"Single code-body check passed {cases.Length} host-free cases (compile only).");
  }

  private static int Check(string code, string path, IReadOnlyList<MetadataReference> references, out string message)
  {
    if (string.IsNullOrWhiteSpace(code))
    {
      message = $"C# code file is empty: {path}";
      return 1;
    }

    if (Civil3DMcpPlugin.ScriptInstrumentation.FindAwait(code) is { } awaitPosition)
    {
      message =
        $"{path}({awaitPosition.Line},{awaitPosition.Column}): error MCP0001: 'await' is not supported; " +
        "scripts run synchronously on the Civil 3D main thread.";
      return 1;
    }

    var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
      .WithUsings(Imports)
      .WithNullableContextOptions(NullableContextOptions.Disable);

    // Like the plugin: with the default aliases first, and without them when
    // an error involves an aliased name.
    Compilation? reported = null;
    foreach (var withAliases in new[] { true, false })
    {
      var prelude = (withAliases
          ? string.Join("\n", DefaultTypeAliases.Select(a => $"using {a.Name} = global::{a.Target};")) + "\n"
          : "") + GlobalsPrelude;
      var preludeCompilation = CSharpCompilation.CreateScriptCompilation(
        "Civil3dSingleCodePrelude",
        CSharpSyntaxTree.ParseText(prelude, ScriptParse),
        references,
        options);
      if (HasErrors(preludeCompilation.GetDiagnostics()))
      {
        message = "The script stand-in did not compile:" + Environment.NewLine +
          Format(preludeCompilation.GetDiagnostics());
        return 2;
      }

      var compilation = CSharpCompilation.CreateScriptCompilation(
        "Civil3dSingleCodeProbe",
        CSharpSyntaxTree.ParseText(code, ScriptParse, path),
        references,
        options,
        preludeCompilation,
        typeof(object));
      var diagnostics = compilation.GetDiagnostics();
      if (!HasErrors(diagnostics))
      {
        if (Civil3DMcpPlugin.ScriptSandbox.FindViolation(compilation) is { } violation)
        {
          message = $"{path}({violation.Line}): the script guard refuses '{violation.Name}': {violation.Reason}.";
          return 1;
        }
        message = $"Civil 3D single C# code-body compilation passed: {path}";
        return 0;
      }

      reported ??= compilation;
      if (!withAliases || !MentionsDefaultAlias(diagnostics)) break;
    }

    message = Format(reported!.GetDiagnostics());
    return 1;
  }

  private static bool HasErrors(IEnumerable<Diagnostic> diagnostics)
    => diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

  private static bool MentionsDefaultAlias(IEnumerable<Diagnostic> diagnostics)
    => diagnostics
      .Where(d => d.Severity == DiagnosticSeverity.Error)
      .Select(d => d.GetMessage())
      .Any(message => DefaultTypeAliases.Any(a => message.Contains($"'{a.Name}'", StringComparison.Ordinal)));

  private static string Format(IEnumerable<Diagnostic> diagnostics)
    => string.Join(Environment.NewLine, diagnostics
      .Where(d => d.Severity == DiagnosticSeverity.Error)
      .Select(d => d.ToString()));
}
