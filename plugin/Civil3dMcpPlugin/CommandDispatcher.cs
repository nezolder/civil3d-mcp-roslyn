using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// Routes the private JSON-RPC methods used by the MCP host.
/// </summary>
public static class CommandDispatcher
{
  public static Task<object?> DispatchAsync(
    string method,
    JsonObject? parameters,
    CancellationToken cancellationToken)
    => DispatchAsync(method, parameters, cancellationToken, null);

  internal static async Task<object?> DispatchAsync(
    string method,
    JsonObject? parameters,
    CancellationToken cancellationToken,
    InternalBenchmarkMeasurement? benchmarkMeasurement,
    OperationProgress? progress = null)
  {
    return method switch
    {
      "executeCode" => await ExecuteCodeAsync(parameters, benchmarkMeasurement, progress, cancellationToken),
      "getCivil3DHealth" => await GetHealthAsync(),
      "getActiveDrawingIdentity" => await GetActiveDrawingIdentityAsync(progress, cancellationToken),
      "apiLookup" => ApiLookup.Lookup(parameters),

      _ => throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Unknown method '{method}'. Available: executeCode, getCivil3DHealth, getActiveDrawingIdentity, apiLookup"
      ),
    };
  }

  /// <summary>
  /// Execute C# code via Roslyn in the Civil 3D context.
  /// </summary>
  private static async Task<object?> ExecuteCodeAsync(
    JsonObject? parameters,
    InternalBenchmarkMeasurement? benchmarkMeasurement,
    OperationProgress? progress,
    CancellationToken cancellationToken)
  {
    var code = PluginRuntime.GetRequiredString(parameters, "code");
    benchmarkMeasurement?.RecordCode(code);
    var readOnly = PluginRuntime.GetOptionalBool(parameters, "readOnly") ?? false;
    var saveDrawing = PluginRuntime.GetOptionalBool(parameters, "saveDrawing") ?? false;
    if (readOnly && saveDrawing)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "Parameter 'saveDrawing' is only valid when readOnly is false."
      );
    }
    var expectedDrawing = DrawingGuard.Parse(parameters, required: !readOnly);
    // Keep the plugin debug output free of caller-provided description text.
    System.Diagnostics.Debug.WriteLine($"[C3D-MCP] {(readOnly ? "QUERY" : "EXECUTE")}");

    // Execute on Civil 3D main thread with proper document locking
    return await CivilExecution.ExecuteAsync((doc, civilDoc, db, tr) =>
    {
      var context = new ScriptContext(doc, civilDoc, db, tr);

      // Run the Roslyn script synchronously within the command context
      // (we're already on the main thread here)
      var rawResult = RoslynExecutor.ExecuteAsync(code, context, benchmarkMeasurement, progress)
        .GetAwaiter()
        .GetResult();
      progress?.SetStage(OperationStage.SerializingResult);
      return ResultSerializer.Serialize(rawResult);
    }, write: !readOnly, expectedDrawing, benchmarkMeasurement, saveDrawing, progress, cancellationToken);
  }

  /// <summary>
  /// Returns listener and serialized-operation status without touching the
  /// Civil API, command context, active document, document lock, or transaction.
  /// </summary>
  private static Task<object?> GetHealthAsync()
  {
    var status = PluginRuntime.GetStatus();
    return Task.FromResult<object?>(new
    {
      connected = true,
      listenerRunning = status.IsRunning,
      operationInProgress = status.OperationInProgress,
      currentOperation = status.CurrentOperation,
      operationStage = status.OperationStage,
      operationElapsedMs = status.OperationElapsedMs,
      stageElapsedMs = status.StageElapsedMs,
      completionDiagnostics = status.CompletionDiagnostics,
      lastCompletionDiagnostics = status.LastCompletionDiagnostics,
      queueDepth = status.QueueDepth,
      instanceId = status.InstanceId,
      processId = status.ProcessId,
      port = status.Port,
      startedAtUtc = status.StartedAtUtc,
      mode = "code_execution",
      roslyn = true,
      executionBackend = CivilExecution.UseNativeBackend ? "native" : "modal",
    });
  }

  /// <summary>
  /// Returns the active drawing identity without compiling or running caller
  /// code. Unlike health, this intentionally enters the serialized Civil
  /// command context so the identity belongs to the selected live instance.
  /// </summary>
  private static async Task<object?> GetActiveDrawingIdentityAsync(
    OperationProgress? progress,
    CancellationToken cancellationToken)
  {
    return await CivilExecution.ExecuteAsync((doc, civilDoc, database, transaction) => new
    {
      instanceId = PluginRuntime.InstanceId,
      databaseFilename = database.Filename ?? string.Empty,
      fingerprintGuid = database.FingerprintGuid.ToString(),
    }, write: false, expectedDrawing: null, benchmarkMeasurement: null, progress: progress,
      commandContextStartCancellationToken: cancellationToken);
  }
}
