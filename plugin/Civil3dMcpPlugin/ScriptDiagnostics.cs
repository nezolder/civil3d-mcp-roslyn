using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
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

  // Missing-type hints only look where Civil 3D scripts find their types.
  private static readonly string[] HintNamespaceRoots = { "Autodesk", "System" };

  private static readonly object _typeIndexSync = new();
  private static IReadOnlyDictionary<string, string[]>? _typeIndex;
  private static int _typeIndexAssemblyCount = -1;

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

  public static string DescribeRuntimeFailure(Script script, Exception exception)
  {
    var error = Unwrap(exception);
    int? line = null;
    try
    {
      line = FindScriptLine(script.GetCompilation(), error, exception);
    }
    catch (Exception)
    {
      // The line is best effort. Type and message are still reported.
    }
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
          "CS0246" or "CS0103" => DescribeMissingType(diagnostic),
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

  private static string? DescribeMissingType(Diagnostic diagnostic)
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

    var namespaces = NamespacesDeclaringPublicType(name);
    if (namespaces.Count == 0) return null;

    return $"'{name}' is a type in {string.Join(", ", namespaces)}. " +
      $"Add a using line such as `using {namespaces[0]};` or write the full name.";
  }

  /// <summary>
  /// Namespaces that declare a public top-level type of this name. Walking the
  /// compiler's namespace symbols took most of a second per error in Civil 3D,
  /// so the names are indexed once per set of loaded assemblies instead.
  /// </summary>
  private static IReadOnlyList<string> NamespacesDeclaringPublicType(string typeName)
  {
    var assemblies = RoslynExecutor.GetReferenceAssemblies();
    IReadOnlyDictionary<string, string[]> index;
    lock (_typeIndexSync)
    {
      if (_typeIndex is null || _typeIndexAssemblyCount != assemblies.Length)
      {
        _typeIndex = BuildTypeIndex(assemblies);
        _typeIndexAssemblyCount = assemblies.Length;
      }
      index = _typeIndex;
    }
    return index.TryGetValue(typeName, out var namespaces)
      ? namespaces.Take(MaximumNamespaces).ToList()
      : Array.Empty<string>();
  }

  private static IReadOnlyDictionary<string, string[]> BuildTypeIndex(IEnumerable<Assembly> assemblies)
  {
    var namespacesByName = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
    foreach (var assembly in assemblies)
    {
      Type[] types;
      try
      {
        types = assembly.GetExportedTypes();
      }
      catch (Exception)
      {
        // A host assembly whose dependencies cannot load offers no hints.
        continue;
      }

      foreach (var type in types)
      {
        if (type.IsNested || type.Namespace is not { } ns || !IsHintNamespace(ns)) continue;
        var tick = type.Name.IndexOf('`');
        var name = tick < 0 ? type.Name : type.Name[..tick];
        if (!namespacesByName.TryGetValue(name, out var namespaces))
        {
          namespacesByName[name] = namespaces = new SortedSet<string>(StringComparer.Ordinal);
        }
        namespaces.Add(ns);
      }
    }
    return namespacesByName.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
  }

  private static bool IsHintNamespace(string ns) => HintNamespaceRoots.Any(root =>
    ns.Length == root.Length ? ns == root : ns.StartsWith(root + ".", StringComparison.Ordinal));

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

  /// <summary>
  /// Maps the failing script frame to a source line. Scripts run without debug
  /// information, which cost about 200 ms per compilation in Civil 3D; only
  /// after a failure is the same compilation emitted again with a PDB, which
  /// yields the same method tokens and IL offsets.
  /// </summary>
  private static int? FindScriptLine(Compilation compilation, params Exception[] exceptions)
  {
    var frames = exceptions
      .Distinct()
      .SelectMany(exception => new StackTrace(exception, fNeedFileInfo: false).GetFrames())
      .Where(frame => frame.GetILOffset() != StackFrame.OFFSET_UNKNOWN
        && frame.GetMethod()?.Module.Assembly.GetName().Name == compilation.AssemblyName)
      .ToList();
    if (frames.Count == 0) return null;

    using var peStream = new MemoryStream();
    using var pdbStream = new MemoryStream();
    var emitted = compilation.Emit(
      peStream,
      pdbStream,
      options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
    if (!emitted.Success) return null;

    pdbStream.Position = 0;
    using var pdb = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
    var reader = pdb.GetMetadataReader();
    foreach (var frame in frames)
    {
      var method = (MethodDefinitionHandle)MetadataTokens.EntityHandle(frame.GetMethod()!.MetadataToken);
      int? line = null;
      foreach (var point in reader.GetMethodDebugInformation(method).GetSequencePoints())
      {
        if (point.IsHidden) continue;
        if (point.Offset > frame.GetILOffset()) break;
        line = point.StartLine;
      }
      if (line is not null) return line;
    }
    return null;
  }
}
