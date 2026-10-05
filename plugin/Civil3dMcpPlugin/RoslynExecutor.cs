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
  private static ScriptOptions? _options;
  private static int _optionsAssemblyCount = -1;

  /// <summary>Max script execution time (default 120 seconds).</summary>
  public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);

  /// <summary>
  /// Return ScriptOptions referencing every loaded assembly. The options are
  /// reused until another assembly loads, so Roslyn can keep the metadata it
  /// already read for those references.
  /// </summary>
  private static ScriptOptions GetOptions()
  {
    var loadedAssemblies = GetReferenceAssemblies();

    lock (_optionsSync)
    {
      if (_options is null || _optionsAssemblyCount != loadedAssemblies.Length)
      {
        _options = BuildOptions(loadedAssemblies);
        _optionsAssemblyCount = loadedAssemblies.Length;
      }
      return _options;
    }
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
    CancellationToken cancellationToken = default)
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
      var options = GetOptions();
      var instrumented = ScriptInstrumentation.AddCancellationCheckpoints(code);
      script = CSharpScript.Create<object>(instrumented, options, typeof(ScriptContext));
      bool failed;
      try
      {
        failed = HasErrors(script.Compile()); // Pre-compile for better error messages
        if (failed && !string.Equals(instrumented, code, StringComparison.Ordinal))
        {
          // Report errors at positions in the caller's own text, and never let
          // a checkpoint turn a valid script into a failing one.
          script = CSharpScript.Create<object>(code, options, typeof(ScriptContext));
          failed = HasErrors(script.Compile());
        }
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

    // Execute with timeout. Loop checkpoints also observe the caller's token,
    // which the TCP server cancels when the client disconnects.
    using var timeout = new CancellationTokenSource(Timeout);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
    var previousToken = ScriptCancellation.Token;
    ScriptCancellation.Token = cts.Token;

    try
    {
      progress?.SetStage(OperationStage.RunningScript);
      var result = await script!.RunAsync(context, cts.Token);
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
        $"Script execution timed out after {Timeout.TotalSeconds}s."
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

  private static bool HasErrors(IEnumerable<Diagnostic> diagnostics)
    => diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

  internal static int CachedScriptCount => _scriptCache.Count;

  /// <summary>Clear the script cache.</summary>
  public static void ClearCache()
  {
    _scriptCache.Clear();
  }
}
