using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;

namespace Civil3DMcpPlugin;

/// <summary>
/// Turns script compilation and runtime failures into messages that point at
/// the fix, so a caller can correct a script without an extra lookup round.
/// </summary>
internal static class ScriptDiagnostics
{
  internal const int MaximumHints = 10;
  private const int MaximumSimilarMembers = 5;
  private const int MaximumNamespaces = 3;

  // Roslyn loads each compiled script as an in-memory assembly named
  // "ℛ*<guid>#<submission>-<n>"; only those frames are caller code.
  private const string ScriptAssemblyPrefix = "ℛ*";

  // Missing-type hints only look where Civil 3D scripts find their types.
  private static readonly string[] HintNamespaceRoots = { "Autodesk", "System" };

  public static string DescribeCompilationFailure(Script script, IReadOnlyList<Diagnostic> diagnostics)
  {
    var message = new StringBuilder("C# compilation failed:\n");
    message.Append(string.Join("\n", diagnostics.Select(d => d.ToString())));

    var hints = BuildHints(script, diagnostics);
    if (hints.Count > 0)
    {
      message.Append("\nHints:");
      foreach (var hint in hints) message.Append("\n- ").Append(hint);
    }
    return message.ToString();
  }

  public static string DescribeRuntimeFailure(Exception exception)
  {
    var error = Unwrap(exception);
    var line = FindScriptLine(error) ?? FindScriptLine(exception);
    var location = line is int value ? $" at script line {value}" : string.Empty;
    return $"Script threw {error.GetType().FullName}{location}: {error.Message}";
  }

  private static List<string> BuildHints(Script script, IReadOnlyList<Diagnostic> diagnostics)
  {
    var hints = new List<string>();
    try
    {
      var compilation = script.GetCompilation();
      foreach (var diagnostic in diagnostics)
      {
        if (hints.Count >= MaximumHints) break;
        var hint = diagnostic.Id switch
        {
          "CS1061" or "CS0117" => DescribeMissingMember(compilation, diagnostic),
          "CS0246" or "CS0103" => DescribeMissingType(compilation, diagnostic),
          _ => null,
        };
        if (hint is not null && !hints.Contains(hint)) hints.Add(hint);
      }
    }
    catch (Exception)
    {
      // Hints are best effort. The compiler errors themselves are still reported.
    }
    return hints;
  }

  private static string? DescribeMissingMember(Compilation compilation, Diagnostic diagnostic)
  {
    var tree = diagnostic.Location.SourceTree;
    if (tree is null) return null;
    var access = tree.GetRoot()
      .FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
      .AncestorsAndSelf()
      .OfType<MemberAccessExpressionSyntax>()
      .FirstOrDefault();
    if (access is null) return null;

    var model = compilation.GetSemanticModel(tree);
    var type = model.GetTypeInfo(access.Expression).Type
      ?? model.GetSymbolInfo(access.Expression).Symbol as ITypeSymbol;
    if (type is null || type.TypeKind == TypeKind.Error) return null;

    var missing = access.Name.Identifier.ValueText;
    var similar = GetMemberNames(type)
      .Where(name => IsSimilar(name, missing))
      .OrderBy(name => EditDistance(name.ToLowerInvariant(), missing.ToLowerInvariant()))
      .ThenBy(name => name, StringComparer.Ordinal)
      .Take(MaximumSimilarMembers)
      .ToList();
    if (similar.Count == 0) return null;

    var typeName = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
    return $"{typeName} has no member '{missing}'. Similar members, including inherited: {string.Join(", ", similar)}.";
  }

  private static string? DescribeMissingType(Compilation compilation, Diagnostic diagnostic)
  {
    var tree = diagnostic.Location.SourceTree;
    if (tree is null) return null;
    var name = tree.GetRoot()
      .FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
      .AncestorsAndSelf()
      .OfType<SimpleNameSyntax>()
      .FirstOrDefault()?
      .Identifier.ValueText;
    if (string.IsNullOrEmpty(name)) return null;

    var namespaces = compilation.GlobalNamespace.GetNamespaceMembers()
      .Where(root => HintNamespaceRoots.Contains(root.Name, StringComparer.Ordinal))
      .SelectMany(root => NamespacesDeclaringPublicType(root, name))
      .Distinct(StringComparer.Ordinal)
      .OrderBy(ns => ns, StringComparer.Ordinal)
      .Take(MaximumNamespaces)
      .ToList();
    if (namespaces.Count == 0) return null;

    return $"'{name}' is a type in {string.Join(", ", namespaces)}. " +
      $"Add a using line such as `using {namespaces[0]};` or write the full name.";
  }

  private static IEnumerable<string> NamespacesDeclaringPublicType(INamespaceSymbol root, string typeName)
  {
    var pending = new Stack<INamespaceSymbol>();
    pending.Push(root);
    while (pending.Count > 0)
    {
      var current = pending.Pop();
      if (current.GetTypeMembers(typeName).Any(type => type.DeclaredAccessibility == Accessibility.Public))
      {
        yield return current.ToDisplayString();
      }
      foreach (var child in current.GetNamespaceMembers()) pending.Push(child);
    }
  }

  private static IEnumerable<string> GetMemberNames(ITypeSymbol type)
  {
    var types = new List<ITypeSymbol>();
    for (var current = type; current is not null; current = current.BaseType) types.Add(current);
    types.AddRange(type.AllInterfaces);

    return types
      .SelectMany(t => t.GetMembers())
      .Where(member => member.DeclaredAccessibility == Accessibility.Public && member.CanBeReferencedByName)
      .Where(member => member is IPropertySymbol or IFieldSymbol or IEventSymbol
        || member is IMethodSymbol { MethodKind: MethodKind.Ordinary })
      .Select(member => member.Name)
      .Distinct(StringComparer.Ordinal);
  }

  private static bool IsSimilar(string candidate, string missing)
  {
    if (candidate.Length >= 3 && missing.Length >= 3 &&
        (candidate.Contains(missing, StringComparison.OrdinalIgnoreCase) ||
         missing.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
    {
      return true;
    }
    var allowed = Math.Max(1, missing.Length / 3);
    return EditDistance(candidate.ToLowerInvariant(), missing.ToLowerInvariant()) <= allowed;
  }

  /// <summary>
  /// Optimal string alignment distance: a swap of two adjacent letters, the
  /// most common typo in member names, counts as one edit.
  /// </summary>
  private static int EditDistance(string left, string right)
  {
    var beforePrevious = new int[right.Length + 1];
    var previous = new int[right.Length + 1];
    var current = new int[right.Length + 1];
    for (var j = 0; j <= right.Length; j++) previous[j] = j;
    for (var i = 1; i <= left.Length; i++)
    {
      current[0] = i;
      for (var j = 1; j <= right.Length; j++)
      {
        var cost = left[i - 1] == right[j - 1] ? 0 : 1;
        current[j] = Math.Min(previous[j - 1] + cost, Math.Min(previous[j] + 1, current[j - 1] + 1));
        if (i > 1 && j > 1 && left[i - 1] == right[j - 2] && left[i - 2] == right[j - 1])
        {
          current[j] = Math.Min(current[j], beforePrevious[j - 2] + 1);
        }
      }
      (beforePrevious, previous, current) = (previous, current, beforePrevious);
    }
    return previous[right.Length];
  }

  private static Exception Unwrap(Exception exception)
  {
    while (true)
    {
      if (exception is TargetInvocationException { InnerException: { } invocationInner })
      {
        exception = invocationInner;
      }
      else if (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
      {
        exception = aggregate.InnerExceptions[0];
      }
      else
      {
        return exception;
      }
    }
  }

  private static int? FindScriptLine(Exception exception)
  {
    foreach (var frame in new StackTrace(exception, fNeedFileInfo: true).GetFrames())
    {
      var assemblyName = frame.GetMethod()?.DeclaringType?.Assembly.GetName().Name;
      if (assemblyName?.StartsWith(ScriptAssemblyPrefix, StringComparison.Ordinal) != true) continue;
      var line = frame.GetFileLineNumber();
      if (line > 0) return line;
    }
    return null;
  }
}
