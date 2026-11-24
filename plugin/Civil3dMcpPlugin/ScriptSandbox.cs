using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Civil3DMcpPlugin;

/// <summary>
/// Guard rail for script code: refuses a script that names a type or member
/// this plugin does not want to run inside Civil 3D, such as starting a
/// process or deleting a file. It checks what the compiler resolved, not the
/// script text, so a comment, a string or a property that only looks similar
/// (<c>process.StartStation</c>, <c>styleRegistry.Add</c>) is not refused, and
/// a short name behind a <c>using</c> line (<c>File.Delete</c>) is.
///
/// This is not a security boundary. A script runs with the rights of the
/// Civil 3D process and can still reach these operations indirectly, for
/// example through reflection. The guard catches the plain, accidental case.
/// </summary>
public static class ScriptSandbox
{
  /// <summary>A refused name: why, and on which script line it stands.</summary>
  internal sealed record Violation(string Reason, string Name, int Line);

  private const string ProcessReason = "Starting or controlling processes is not allowed";
  private const string DeleteReason = "Deleting files or folders is not allowed";
  private const string HttpReason = "HTTP requests are not allowed";
  private const string SocketReason = "Socket operations are not allowed";
  private const string LoadReason = "Loading assemblies is not allowed";
  private const string NativeReason = "Calling native code (P/Invoke) is not allowed";
  private const string ExitReason = "Ending the Civil 3D process is not allowed";
  private const string RegistryReason = "Registry access is not allowed";
  private const string DomainReason = "Creating AppDomains is not allowed";

  /// <summary>Types refused as a whole, and every type derived from them.</summary>
  private static readonly Dictionary<string, string> BlockedTypes = new(StringComparer.Ordinal)
  {
    ["System.Diagnostics.Process"] = ProcessReason,
    ["System.Diagnostics.ProcessStartInfo"] = ProcessReason,
    ["System.Net.WebClient"] = HttpReason,
    ["System.Net.WebRequest"] = HttpReason,
    ["System.Runtime.Loader.AssemblyLoadContext"] = LoadReason,
    ["System.Runtime.InteropServices.DllImportAttribute"] = NativeReason,
    ["System.Runtime.InteropServices.LibraryImportAttribute"] = NativeReason,
    ["System.Runtime.InteropServices.Marshal"] = NativeReason,
    ["System.Runtime.InteropServices.NativeLibrary"] = NativeReason,
    ["Microsoft.Win32.Registry"] = RegistryReason,
    ["Microsoft.Win32.RegistryKey"] = RegistryReason,
  };

  /// <summary>Namespaces whose every type is refused.</summary>
  private static readonly (string Namespace, string Reason)[] BlockedNamespaces =
  {
    ("System.Net.Http", HttpReason),
    ("System.Net.Sockets", SocketReason),
  };

  /// <summary>Single members of types that are otherwise allowed.</summary>
  private static readonly Dictionary<(string Type, string Member), string> BlockedMembers = new()
  {
    [("System.IO.File", "Delete")] = DeleteReason,
    [("System.IO.Directory", "Delete")] = DeleteReason,
    [("System.IO.FileSystemInfo", "Delete")] = DeleteReason,
    [("System.IO.FileInfo", "Delete")] = DeleteReason,
    [("System.IO.DirectoryInfo", "Delete")] = DeleteReason,
    [("System.Reflection.Assembly", "Load")] = LoadReason,
    [("System.Reflection.Assembly", "LoadFrom")] = LoadReason,
    [("System.Reflection.Assembly", "LoadFile")] = LoadReason,
    [("System.Reflection.Assembly", "UnsafeLoadFrom")] = LoadReason,
    [("System.Reflection.Assembly", "LoadWithPartialName")] = LoadReason,
    [("System.Environment", "Exit")] = ExitReason,
    [("System.Environment", "FailFast")] = ExitReason,
    [("System.AppDomain", "CreateDomain")] = DomainReason,
    [("System.AppDomain", "ExecuteAssembly")] = LoadReason,
    [("System.AppDomain", "ExecuteAssemblyByName")] = LoadReason,
    [("System.AppDomain", "Load")] = LoadReason,
  };

  private const string SectionSourcesReason =
    "SampleLineGroup.GetSectionSources needs the group opened for write; in a read-only call it has " +
    "closed Civil 3D 2025 with a fatal eNotOpenForWrite. Read the list in civil3d_execute with the group " +
    "opened ForWrite; with preview: true nothing is kept";

  /// <summary>
  /// Members refused in civil3d_query only. They are safe in a write call,
  /// where the script can open the object for write, but have crashed
  /// Civil 3D when a read-only call reached them.
  /// </summary>
  private static readonly Dictionary<(string Type, string Member), string> QueryBlockedMembers = new()
  {
    [("Autodesk.Civil.DatabaseServices.SampleLineGroup", "GetSectionSources")] = SectionSourcesReason,
  };

  /// <summary>
  /// The check that needs no compiler: there must be code at all.
  /// </summary>
  public static void Validate(string code)
  {
    if (string.IsNullOrWhiteSpace(code))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "Code cannot be empty."
      );
    }
  }

  /// <summary>
  /// Refuses a compiled script that uses a blocked type or member. Called
  /// before the script first runs; a script that passed is cached with its
  /// compilation and is not checked again.
  /// </summary>
  public static void Validate(Compilation compilation)
  {
    if (FindViolation(compilation) is not { } violation) return;
    throw new JsonRpcDispatchException(
      "CIVIL3D.SANDBOX_VIOLATION",
      $"Script blocked: {violation.Reason}. '{violation.Name}' at script line {violation.Line}. Nothing was run."
    );
  }

  /// <summary>
  /// Refuses a compiled script for civil3d_query when it uses a member that
  /// is only safe in a write call. Runs on every query, cached or not, since
  /// the same script text may also be sent as a write; scripts that do not
  /// even mention such a member skip the semantic walk.
  /// </summary>
  public static void ValidateForQuery(string code, Compilation compilation)
  {
    if (!QueryBlockedMembers.Keys.Any(key => code.Contains(key.Member, StringComparison.Ordinal))) return;
    if (FindViolation(compilation, QueryReasonFor) is not { } violation) return;
    throw new JsonRpcDispatchException(
      "CIVIL3D.SANDBOX_VIOLATION",
      $"Script blocked in civil3d_query: {violation.Reason}. '{violation.Name}' at script line {violation.Line}. Nothing was run."
    );
  }

  /// <summary>The first blocked name in the script, in reading order, or null.</summary>
  internal static Violation? FindViolation(Compilation compilation)
    => FindViolation(compilation, ReasonFor);

  private static Violation? FindViolation(Compilation compilation, Func<ISymbol, string?> reasonFor)
  {
    foreach (var tree in compilation.SyntaxTrees)
    {
      var model = compilation.GetSemanticModel(tree);
      foreach (var node in tree.GetRoot().DescendantNodes())
      {
        // Every use of a type or member is written as a name, except a
        // target-typed "new()", which names nothing.
        if (node is not (SimpleNameSyntax or ImplicitObjectCreationExpressionSyntax)) continue;

        var info = model.GetSymbolInfo(node);
        var symbols = info.Symbol is not null
          ? new[] { info.Symbol }
          : info.CandidateSymbols.ToArray();
        foreach (var symbol in symbols)
        {
          if (reasonFor(symbol) is not { } reason) continue;
          // Loop checkpoints never add a line break, so the line is the caller's own.
          var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
          return new Violation(reason, DisplayName(symbol), line);
        }
      }
    }
    return null;
  }

  private static string? ReasonFor(ISymbol symbol)
  {
    if (symbol is INamespaceSymbol or ILocalSymbol or IParameterSymbol or ILabelSymbol or IRangeVariableSymbol)
    {
      // A namespace alone does nothing, and a variable was already checked
      // where its type or its value was named.
      return null;
    }

    var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
    if (type is null) return null;

    if (symbol is not INamedTypeSymbol)
    {
      for (var owner = type; owner is not null; owner = owner.BaseType)
      {
        if (BlockedMembers.TryGetValue((FullName(owner), symbol.Name), out var memberReason)) return memberReason;
      }
    }

    for (var current = type; current is not null; current = current.BaseType)
    {
      if (BlockedTypes.TryGetValue(FullName(current), out var typeReason)) return typeReason;
      var containingNamespace = current.ContainingNamespace?.ToDisplayString() ?? string.Empty;
      foreach (var (blocked, namespaceReason) in BlockedNamespaces)
      {
        if (containingNamespace == blocked
          || containingNamespace.StartsWith(blocked + ".", StringComparison.Ordinal))
        {
          return namespaceReason;
        }
      }
    }
    return null;
  }

  private static string? QueryReasonFor(ISymbol symbol)
  {
    if (symbol is not (IMethodSymbol or IPropertySymbol) || symbol.ContainingType is null) return null;
    for (var owner = symbol.ContainingType; owner is not null; owner = owner.BaseType)
    {
      if (QueryBlockedMembers.TryGetValue((FullName(owner), symbol.Name), out var reason)) return reason;
    }
    return null;
  }

  /// <summary>Namespace and name without type arguments, such as System.IO.File.</summary>
  private static string FullName(INamedTypeSymbol type)
  {
    var definition = type.OriginalDefinition;
    var name = definition.Name;
    for (var outer = definition.ContainingType; outer is not null; outer = outer.ContainingType)
    {
      name = outer.Name + "." + name;
    }
    var containingNamespace = definition.ContainingNamespace;
    return containingNamespace is null || containingNamespace.IsGlobalNamespace
      ? name
      : containingNamespace.ToDisplayString() + "." + name;
  }

  private static string DisplayName(ISymbol symbol)
  {
    if (symbol is INamedTypeSymbol type) return FullName(type);
    var owner = symbol.ContainingType is null ? string.Empty : FullName(symbol.ContainingType) + ".";
    return symbol is IMethodSymbol { MethodKind: MethodKind.Constructor }
      ? owner.TrimEnd('.')
      : owner + symbol.Name;
  }
}
