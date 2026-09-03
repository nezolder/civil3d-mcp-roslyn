using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var civilReferenceDirectory = Path.Combine(repositoryRoot, "C_References");
var civilReferencePaths = new[]
{
  "accoremgd.dll",
  "AcDbMgd.dll",
  "acmgd.dll",
  "AecBaseMgd.dll",
  "AeccDbMgd.dll",
}.Select(name => Path.Combine(civilReferenceDirectory, name)).ToArray();

if (civilReferencePaths.Any(path => !File.Exists(path)))
{
  Console.WriteLine("Civil 3D skill template compilation skipped: local C_References DLLs are incomplete.");
  return;
}

var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
  .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
  ?? Array.Empty<string>();
var references = trustedPlatformAssemblies
  .Concat(civilReferencePaths)
  .Distinct(StringComparer.OrdinalIgnoreCase)
  .Select(path => MetadataReference.CreateFromFile(path))
  .ToArray();

var codeBlockPattern = new Regex(
  "```csharp\\s*\\r?\\n([\\s\\S]*?)\\r?\\n```",
  RegexOptions.Compiled | RegexOptions.CultureInvariant
);
var syntaxTrees = new List<SyntaxTree>();
var blockCount = 0;

foreach (var skillPath in Directory.EnumerateFiles(
  Path.Combine(repositoryRoot, "skills"),
  "*.skill.md",
  SearchOption.AllDirectories
).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
{
  var relativePath = Path.GetRelativePath(repositoryRoot, skillPath);
  var markdown = File.ReadAllText(skillPath);
  var matches = codeBlockPattern.Matches(markdown);

  for (var index = 0; index < matches.Count; index++)
  {
    blockCount++;
    var code = matches[index].Groups[1].Value;
    var className = $"SkillTemplate{blockCount}";
    var source = $$"""
      #nullable disable
      using System;
      using System.Linq;
      using System.Collections.Generic;
      using System.Text;
      using Autodesk.AutoCAD.ApplicationServices;
      using Autodesk.AutoCAD.DatabaseServices;
      using Autodesk.AutoCAD.EditorInput;
      using Autodesk.AutoCAD.Geometry;
      using Autodesk.AutoCAD.Runtime;
      using Autodesk.Civil;
      using Autodesk.Civil.ApplicationServices;
      using Autodesk.Civil.DatabaseServices;
      using Autodesk.Civil.Settings;

      public static class {{className}}
      {
        public static object Run(
          Document Document,
          CivilDocument CivilDoc,
          Database Database,
          Transaction Transaction,
          Editor Editor)
        {
      {{code}}
        }
      }
      """;

    syntaxTrees.Add(CSharpSyntaxTree.ParseText(
      source,
      new CSharpParseOptions(LanguageVersion.Latest),
      $"{relativePath}#csharp-{index + 1}"
    ));
  }
}

if (blockCount == 0)
  throw new InvalidOperationException("No C# skill templates were found.");

var compilation = CSharpCompilation.Create(
  "Civil3dSkillTemplateChecks",
  syntaxTrees,
  references,
  new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
);
var errors = compilation.GetDiagnostics()
  .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
  .ToArray();

if (errors.Length > 0)
{
  Console.Error.WriteLine(string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
  Environment.ExitCode = 1;
  return;
}

Console.WriteLine($"Civil 3D 2025 metadata compilation passed for {blockCount} C# skill templates.");
try
{
  FixedPrimitiveInputTests.Run(repositoryRoot,
    trustedPlatformAssemblies.Select(path => MetadataReference.CreateFromFile(path)));
}
catch (Exception error)
{
  Console.Error.WriteLine(error.Message);
  Environment.ExitCode = 1;
}

static string FindRepositoryRoot(string startPath)
{
  for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
  {
    if (Directory.Exists(Path.Combine(directory.FullName, "skills"))
        && Directory.Exists(Path.Combine(directory.FullName, "C_References")))
      return directory.FullName;
  }

  throw new DirectoryNotFoundException("Could not locate the repository root.");
}
