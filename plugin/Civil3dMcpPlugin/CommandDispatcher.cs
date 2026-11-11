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
    OperationProgress? progress = null,
    ExecutionRecord? record = null)
  {
    return method switch
    {
      "executeCode" => await ExecuteCodeAsync(parameters, benchmarkMeasurement, progress, record, cancellationToken),
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
    ExecutionRecord? record,
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
    var timeLimit = ReadScriptTimeLimit(parameters);
    ReadWriteSafetyOptions(parameters, readOnly, saveDrawing, record);
    // Keep the plugin debug output free of caller-provided description text.
    System.Diagnostics.Debug.WriteLine($"[C3D-MCP] {(readOnly ? "QUERY" : "EXECUTE")}");

    // Execute on Civil 3D main thread with proper document locking
    return await CivilExecution.ExecuteAsync((doc, civilDoc, db, tr) =>
    {
      var context = new ScriptContext(doc, civilDoc, db, tr);

      // Run the Roslyn script synchronously within the command context
      // (we're already on the main thread here)
      var rawResult = RoslynExecutor
        .ExecuteAsync(code, context, benchmarkMeasurement, progress, cancellationToken, timeLimit)
        .GetAwaiter()
        .GetResult();
      progress?.SetStage(OperationStage.SerializingResult);
      // A lazy result runs caller code while it is serialized; keep it stoppable.
      return RoslynExecutor.RunWithScriptCancellation(
        () => ResultSerializer.Serialize(rawResult), cancellationToken, timeLimit);
    }, write: !readOnly, expectedDrawing, benchmarkMeasurement, saveDrawing, progress, cancellationToken, record);
  }

  /// <summary>
  /// Optional safety switches of a write: requireClean refuses a drawing with
  /// unsaved changes, backup copies the saved drawing file first. A backup is
  /// taken from the file on disk, so it asks for a clean drawing unless the
  /// caller explicitly sets requireClean to false.
  /// </summary>
  internal static void ReadWriteSafetyOptions(
    JsonObject? parameters,
    bool readOnly,
    bool saveDrawing,
    ExecutionRecord? record)
  {
    var requireClean = PluginRuntime.GetOptionalBool(parameters, "requireClean");
    var backup = PluginRuntime.GetOptionalBool(parameters, "backup") ?? false;
    string? backupDirectory = null;
    var backupDirectoryNode = parameters?["backupDirectory"];
    if (backupDirectoryNode != null)
    {
      if (backupDirectoryNode is not JsonValue backupDirectoryValue
        || !backupDirectoryValue.TryGetValue<string>(out backupDirectory)
        || string.IsNullOrWhiteSpace(backupDirectory)
        || !Path.IsPathFullyQualified(backupDirectory))
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.INVALID_INPUT",
          "Parameter 'backupDirectory' must be an absolute folder path."
        );
      }
    }

    if (readOnly && (requireClean == true || backup || backupDirectory != null))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "Parameters 'requireClean', 'backup' and 'backupDirectory' are only valid when readOnly is false."
      );
    }
    if (backupDirectory != null && !backup)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "Parameter 'backupDirectory' is only valid together with backup: true."
      );
    }

    if (record == null) return;
    record.RequireClean = requireClean ?? backup;
    record.Backup = backup;
    record.BackupDirectory = backupDirectory;
    record.SaveRequested = saveDrawing;
  }

  /// <summary>
  /// Optional per-call script limit. Absent means the executor default; a
  /// value outside the allowed range is refused before anything runs.
  /// </summary>
  internal static TimeSpan? ReadScriptTimeLimit(JsonObject? parameters)
  {
    var value = parameters?["timeoutSeconds"];
    if (value == null) return null;
    if (value is JsonValue jsonValue
      && jsonValue.TryGetValue<int>(out var seconds)
      && seconds >= 1
      && seconds <= RoslynExecutor.MaxTimeoutSeconds)
    {
      return TimeSpan.FromSeconds(seconds);
    }
    throw new JsonRpcDispatchException(
      "CIVIL3D.INVALID_INPUT",
      $"Parameter 'timeoutSeconds' must be an integer between 1 and {RoslynExecutor.MaxTimeoutSeconds}."
    );
  }

  /// <summary>
  /// Returns listener and serialized-operation status without touching the
  /// Civil API, command context, active document, document lock, or transaction.
  /// </summary>
  private static Task<object?> GetHealthAsync()
  {
    var status = PluginRuntime.GetStatus();
    var observation = ActiveDrawingTracker.Current;
    var drawing = observation?.Drawing;
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
      // Which drawing was active when the application was last idle. Read
      // from memory, so asking for it runs no command. False until the first
      // look; a null drawing with true means no drawing is open.
      activeDrawingKnown = observation != null,
      activeDrawing = drawing == null
        ? null
        : new
        {
          databaseFilename = drawing.DatabaseFilename,
          fingerprintGuid = drawing.FingerprintGuid,
          observedAtUtc = drawing.ObservedAtUtc,
        },
      build = new
      {
        commit = BuildInfo.Commit,
        dirty = BuildInfo.Dirty,
        builtAtUtc = BuildInfo.BuiltAtUtc,
      },
      features = Features,
    });
  }

  /// <summary>
  /// Optional abilities of this plugin build. The MCP host checks them before
  /// it relies on one, so that an older plugin cannot silently ignore a
  /// safety switch.
  /// </summary>
  internal static readonly string[] Features = { "activeDrawing", "writeSafety" };

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
