using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Civil3DMcpPlugin;

/// <summary>
/// Compiles and executes C# code snippets using Roslyn.
/// Provides full access to AutoCAD + Civil 3D APIs through ScriptContext globals.
/// Includes script caching for performance.
/// </summary>
public static class RoslynExecutor
{
  /// <summary>Upper bound on cached compiled scripts.</summary>
  internal const int ScriptCacheCapacity = 64;

  /// <summary>Compiled scripts by exact code text; failed compilations are not kept.</summary>
  private static readonly ScriptCache<Script<object>> _scriptCache = new(ScriptCacheCapacity);

  private static readonly object _optionsSync = new();
  private static ScriptEnvironment? _environment;
  private static int _optionsAssemblyCount = -1;

  /// <summary>Longest script limit one call may ask for.</summary>
  public const int MaxTimeoutSeconds = 1_800;

  /// <summary>Script time limit when the call does not set one (default 120 seconds).</summary>
  public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);

  /// <summary>
  /// What a bare type name means when two auto-imported namespaces both
  /// declare it. Without a default the compiler rejects the name as ambiguous
  /// (CS0104) and the caller needs another round. Each default is the meaning
  /// a Civil 3D drawing script almost always wants; a full name or the
  /// caller's own using alias still selects the other type.
  /// </summary>
  internal static readonly (string Name, string Target)[] DefaultTypeAliases =
  {
    ("DBObject", "Autodesk.AutoCAD.DatabaseServices.DBObject"),
    ("Entity", "Autodesk.AutoCAD.DatabaseServices.Entity"),
    ("ErrorStatus", "Autodesk.AutoCAD.Runtime.ErrorStatus"),
    ("Exception", "System.Exception"),
    ("Section", "Autodesk.Civil.DatabaseServices.Section"),
    ("Surface", "Autodesk.Civil.DatabaseServices.Surface"),
  };

  /// <summary>
  /// Compile settings plus an empty first submission that only declares the
  /// default aliases. Caller scripts are compiled as its continuation: using
  /// aliases carry over to later submissions, and the caller's text stays
  /// untouched, so line and column numbers do not move.
  /// </summary>
  private sealed record ScriptEnvironment(ScriptOptions Options, Script? AliasPrelude);

  /// <summary>
  /// Return the script environment referencing every loaded assembly. It is
  /// reused until another assembly loads, so Roslyn can keep the metadata it
  /// already read for those references.
  /// </summary>
  private static ScriptEnvironment GetEnvironment()
  {
    var loadedAssemblies = GetReferenceAssemblies();

    lock (_optionsSync)
    {
      if (_environment is null || _optionsAssemblyCount != loadedAssemblies.Length)
      {
        var options = BuildOptions(loadedAssemblies);
        _environment = new ScriptEnvironment(options, BuildAliasPrelude(options, loadedAssemblies));
        _optionsAssemblyCount = loadedAssemblies.Length;
      }
      return _environment;
    }
  }

  /// <summary>
  /// The alias submission for the types that exist in this host, or null when
  /// there is nothing to alias or it does not compile. Aliases are a
  /// convenience and must never stop scripts from running.
  /// </summary>
  private static Script? BuildAliasPrelude(ScriptOptions options, Assembly[] loadedAssemblies)
  {
    try
    {
      var lines = DefaultTypeAliases
        .Where(alias => TypeExists(alias.Target, loadedAssemblies))
        .Select(alias => $"using {alias.Name} = global::{alias.Target};")
        .ToArray();
      if (lines.Length == 0) return null;

      var prelude = CSharpScript.Create(string.Join("\n", lines), options, typeof(ScriptContext));
      return HasErrors(prelude.Compile()) ? null : prelude;
    }
    catch (Exception)
    {
      return null;
    }
  }

  private static bool TypeExists(string fullName, Assembly[] assemblies)
  {
    foreach (var assembly in assemblies)
    {
      try
      {
        if (assembly.GetType(fullName, throwOnError: false) is not null) return true;
      }
      catch (Exception)
      {
        // An assembly that cannot answer simply does not provide the type.
      }
    }
    return false;
  }

  /// <summary>
  /// Assemblies scripts can reference: everything loaded from a file in the
  /// current AppDomain (Civil 3D loads everything). In-memory script
  /// assemblies have no location and are left out.
  /// </summary>
  internal static Assembly[] GetReferenceAssemblies() => AppDomain.CurrentDomain.GetAssemblies()
    .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
    .ToArray();

  /// <summary>
  /// Build ScriptOptions with all necessary references and imports.
  /// </summary>
  private static ScriptOptions BuildOptions(Assembly[] loadedAssemblies)
  {
    var options = ScriptOptions.Default
      .WithReferences(loadedAssemblies)
      .WithImports(
        // System
        "System",
        "System.Linq",
        "System.Collections.Generic",
        "System.Text",
        // AutoCAD
        "Autodesk.AutoCAD.ApplicationServices",
        "Autodesk.AutoCAD.DatabaseServices",
        "Autodesk.AutoCAD.EditorInput",
        "Autodesk.AutoCAD.Geometry",
        "Autodesk.AutoCAD.Runtime",
        // Civil 3D
        "Autodesk.Civil",
        "Autodesk.Civil.ApplicationServices",
        "Autodesk.Civil.DatabaseServices",
        "Autodesk.Civil.Settings"
      )
      .WithAllowUnsafe(false);

    return options;
  }

  /// <summary>
  /// Execute a C# code snippet with the given ScriptContext as globals.
  /// </summary>
  /// <param name="code">C# code to execute</param>
  /// <param name="context">Globals (Document, CivilDoc, Database, Transaction, Editor)</param>
  /// <returns>The script's return value, or null</returns>
  public static Task<object?> ExecuteAsync(string code, ScriptContext context)
    => ExecuteAsync(code, context, null);

  internal static async Task<object?> ExecuteAsync(
    string code,
    ScriptContext context,
    InternalBenchmarkMeasurement? benchmarkMeasurement,
    OperationProgress? progress = null,
    CancellationToken cancellationToken = default,
    TimeSpan? timeLimit = null)
  {
    progress?.SetStage(OperationStage.PreparingScript);
    // Validate with sandbox
    ScriptSandbox.Validate(code);

    // Try cache first
    var cacheHit = _scriptCache.TryGet(code, out var script);
    benchmarkMeasurement?.RecordCacheLookup(cacheHit);
    if (!cacheHit)
    {
      progress?.SetStage(OperationStage.CompilingScript);
      if (ScriptInstrumentation.FindAwait(code) is { } awaitPosition)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.COMPILATION_ERROR",
          $"C# compilation failed:\n({awaitPosition.Line},{awaitPosition.Column}): error MCP0001: " +
            "'await' is not supported. Scripts run synchronously on the Civil 3D main thread, " +
            "where awaiting can deadlock the application; call the synchronous API instead."
        );
      }
      bool failed;
      try
      {
        // Pre-compile for better error messages.
        script = Compile(code, GetEnvironment(), out failed);
        benchmarkMeasurement?.RecordCompilationAttempt(failed);
      }
      catch
      {
        benchmarkMeasurement?.RecordCompilationAttempt(failed: true);
        throw;
      }
      // A failed script still reaches RunAsync below, which reports its errors.
      if (!failed) _scriptCache.Add(code, script);
    }

    // Execute with a time limit. Loop checkpoints also observe the caller's
    // token, which the TCP server cancels when the client disconnects.
    var limit = timeLimit ?? Timeout;
    using var timeout = new CancellationTokenSource(limit);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
    var previousToken = ScriptCancellation.Token;
    ScriptCancellation.Token = cts.Token;

    try
    {
      progress?.SetStage(OperationStage.RunningScript);
      var result = await script!.RunAsync(context, cts.Token);
      // Roslyn does not look at the token once the script body runs, and a
      // script that reaches no loop checkpoint runs to its end. Past the
      // limit, or after the client gave up, its work must not be committed:
      // the caller has been (or is about to be) told that it was not.
      cts.Token.ThrowIfCancellationRequested();
      return result.ReturnValue;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      // The client is gone. The exception still unwinds before any commit, so
      // the transaction is rolled back; there is nobody left to answer.
      throw;
    }
    catch (OperationCanceledException)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.TIMEOUT",
        $"Script execution timed out after {FormatSeconds(limit)}. {TimeoutAdvice}"
      );
    }
    catch (CompilationErrorException ex)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.COMPILATION_ERROR",
        ScriptDiagnostics.DescribeCompilationFailure(script!, ex.Diagnostics)
      );
    }
    catch (Exception ex) when (ex is not JsonRpcDispatchException)
    {
      // Same code the host already reported for script failures, now with
      // the exception type and the failing script line.
      throw new JsonRpcDispatchException(
        "CIVIL3D.TRANSACTION_FAILED",
        ScriptDiagnostics.DescribeRuntimeFailure(script!, ex)
      );
    }
    finally
    {
      ScriptCancellation.Token = previousToken;
    }
  }

  /// <summary>
  /// Runs work that can execute caller code after the script returned, such
  /// as serializing a lazy LINQ result, under the same loop checkpoints: a
  /// fresh script timeout linked with the caller's token.
  /// </summary>
  internal static T RunWithScriptCancellation<T>(
    Func<T> action,
    CancellationToken cancellationToken,
    TimeSpan? timeLimit = null)
  {
    var limit = timeLimit ?? Timeout;
    using var timeout = new CancellationTokenSource(limit);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
    var previousToken = ScriptCancellation.Token;
    ScriptCancellation.Token = cts.Token;
    try
    {
      var result = action();
      // Same rule as for the script body: no commit past the limit or for a
      // client that is gone.
      cts.Token.ThrowIfCancellationRequested();
      return result;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.TIMEOUT",
        $"Reading the script result timed out after {FormatSeconds(limit)}. {TimeoutAdvice}"
      );
    }
    finally
    {
      ScriptCancellation.Token = previousToken;
    }
  }

  /// <summary>
  /// The limit is reached inside the transaction, so nothing of the script is
  /// committed; say so, and how to allow a known long operation.
  /// </summary>
  private static readonly string TimeoutAdvice =
    "The script was stopped and its transaction was not committed. " +
    $"For a known long operation pass a larger timeoutSeconds (up to {MaxTimeoutSeconds}).";

  /// <summary>
  /// Error text is read by tools and tests, so the number must not follow the
  /// Windows regional setting (a Hungarian system would write "0,2s").
  /// </summary>
  private static string FormatSeconds(TimeSpan limit) =>
    limit.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "s";

  /// <summary>
  /// Compiles the caller's script, preferring loop checkpoints and the default
  /// type aliases. Neither may turn a valid script into a failing one: the
  /// caller's exact text is tried without checkpoints, and when an error
  /// involves an aliased name, without the aliases too. A failure is reported
  /// from the caller's own text, so positions match what was sent.
  /// </summary>
  private static Script<object> Compile(string code, ScriptEnvironment environment, out bool failed)
  {
    var instrumented = ScriptInstrumentation.AddCancellationCheckpoints(code);
    var texts = string.Equals(instrumented, code, StringComparison.Ordinal)
      ? new[] { code }
      : new[] { instrumented, code };

    Script<object>? reported = null;
    foreach (var prelude in environment.AliasPrelude is null
      ? new Script?[] { null }
      : new[] { environment.AliasPrelude, null })
    {
      Script<object>? last = null;
      IEnumerable<Diagnostic> lastDiagnostics = Array.Empty<Diagnostic>();
      foreach (var text in texts)
      {
        last = prelude is null
          ? CSharpScript.Create<object>(text, environment.Options, typeof(ScriptContext))
          : prelude.ContinueWith<object>(text, environment.Options);
        lastDiagnostics = last.Compile();
        if (!HasErrors(lastDiagnostics))
        {
          failed = false;
          return last;
        }
      }

      reported ??= last;
      if (prelude is null || !MentionsDefaultAlias(lastDiagnostics)) break;
    }

    failed = true;
    return reported!;
  }

  private static bool MentionsDefaultAlias(IEnumerable<Diagnostic> diagnostics)
    => diagnostics
      .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
      .Select(diagnostic => diagnostic.GetMessage())
      .Any(message => DefaultTypeAliases.Any(alias =>
        message.Contains($"'{alias.Name}'", StringComparison.Ordinal)));

  private static bool HasErrors(IEnumerable<Diagnostic> diagnostics)
    => diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

  internal static int CachedScriptCount => _scriptCache.Count;

  /// <summary>Clear the script cache.</summary>
  public static void ClearCache()
  {
    _scriptCache.Clear();
  }
}
