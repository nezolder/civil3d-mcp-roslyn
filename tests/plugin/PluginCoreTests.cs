using System.Text.Json.Nodes;
using System.Reflection;
using System.Reflection.Emit;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace Civil3DMcpPlugin;

internal static class PluginCoreTests
{
  private const string ActualPath = @"C:\Projects\Plans\Target.dwg";
  private static readonly Guid ActualFingerprint =
    Guid.Parse("11111111-2222-4333-8444-555555555555");
  private static readonly string EndpointDirectory = Path.Combine(
    Path.GetTempPath(),
    $"civil3d-mcp-plugin-tests-{Guid.NewGuid():N}"
  );

  public static async Task Main()
  {
    var previousEndpointDirectory = Environment.GetEnvironmentVariable(
      EndpointRegistration.DirectoryEnvironmentVariable
    );
    Environment.SetEnvironmentVariable(
      EndpointRegistration.DirectoryEnvironmentVariable,
      EndpointDirectory
    );
    var tests = new (string Name, Func<Task> Run)[]
    {
      ("direct execute guard is required and malformed guards fail closed", DirectGuardContractAsync),
      ("drawing identity is checked before lock, transaction, and Roslyn", DrawingIdentityOrderingAsync),
      ("private drawing identity is read without Roslyn or a commit", PrivateDrawingIdentityAsync),
      ("save runs after commit and releases transaction resources", SaveAfterCommitOrderingAsync),
      ("save rejects unsupported request states before Roslyn", SaveRequestValidationAsync),
      ("save failure preserves committed idempotency state", SaveFailureCompletesIdempotencyAsync),
      ("failed listener start does not report running", FailedListenerStartDoesNotReportRunningAsync),
      ("listener fallback publishes and removes a per-instance endpoint", EndpointRegistrationLifecycleAsync),
      ("parallel dispatch is serialized with exact waiting and active status", SerializedDispatchStatusAsync),
      ("native ExecutionResult bridge closes the lost continuation race", NativeExecutionResultBridgeAsync),
      ("unstarted command context times out without running stale Civil work", CommandContextAdmissionTimeoutAsync),
      ("command-context scheduler failures propagate and late faults are observed", CommandContextAdmissionFaultsAsync),
      ("progress reports a queued AutoCAD command context while health remains responsive", WaitingForCommandContextProgressAsync),
      ("progress keeps the gate through AutoCAD outer-task completion", WaitingForCommandContextCompletionProgressAsync),
      ("progress reports a running script and caller cancellation cannot free its gate", RunningScriptProgressAndCancellationAsync),
      ("progress returns to idle after successful and failed execution", ProgressCleanupAsync),
      ("health bypasses active and waiting Civil operations", HealthBypassesSerializedQueueAsync),
      ("api lookup is metadata-only and bypasses active Civil operations", ApiLookupBypassesSerializedQueueAsync),
      ("cancellation and execution errors release gate and counters", CancellationAndErrorCleanupAsync),
      ("serialized gate wait contributes to commandContextWaitMs", GateWaitMeasurementAsync),
      ("execute results use bounded serialization and structured failures", ExecuteResultSerializationAsync),
      ("idempotency completes only after a successful committed write", IdempotencyFirstSuccessAndCompletedDuplicateAsync),
      ("idempotency rejects an in-progress duplicate before Civil execution", IdempotencyInProgressDuplicateAsync),
      ("idempotency rejects conflicting bindings before Civil execution", IdempotencyConflictAsync),
      ("failed idempotent writes release their reservation", IdempotencyFailureReleasesKeyAsync),
      ("idempotency completed capacity evicts oldest keys deterministically", IdempotencyCapacityEvictionAsync),
      ("idempotency key validation rejects invalid direct JSON-RPC input", IdempotencyInvalidKeyAsync),
    };

    try
    {
      foreach (var (name, run) in tests)
      {
        await run();
        Console.WriteLine($"PASS {name}");
      }

      ResultSerializerTests.RunAll();
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine(ex);
      Environment.ExitCode = 1;
    }
    finally
    {
      PluginRuntime.StopServer();
      Environment.SetEnvironmentVariable(
        EndpointRegistration.DirectoryEnvironmentVariable,
        previousEndpointDirectory
      );
      if (Directory.Exists(EndpointDirectory))
      {
        Directory.Delete(EndpointDirectory, recursive: true);
      }
    }
  }

  private static async Task PrivateDrawingIdentityAsync()
  {
    ResetEnvironment();
    var response = JsonNode.Parse(await SendAsync(new JsonObject
    {
      ["jsonrpc"] = "2.0",
      ["method"] = "getActiveDrawingIdentity",
      ["id"] = "private-drawing-identity",
    }.ToJsonString()))?.AsObject();
    var identity = response?["result"]?.AsObject();

    Assert(identity != null, "private identity must return a result");
    Assert(identity!["instanceId"]?.GetValue<string>() == PluginRuntime.InstanceId,
      "private identity must belong to the current plugin instance");
    Assert(identity["databaseFilename"]?.GetValue<string>() == ActualPath,
      "private identity must return the complete Database.Filename");
    Assert(identity["fingerprintGuid"]?.GetValue<string>() == ActualFingerprint.ToString("B").ToUpperInvariant(),
      "private identity must return Database.FingerprintGuid without formatting assumptions");
    Assert(RoslynExecutor.CallCount == 0, "private identity must not run caller C#");
    Assert(CurrentDocument.LockCount == 1, "private identity must use one document lock");
    Assert(CurrentDatabase.TransactionManager.StartedCount == 1,
      "private identity must use one read transaction");
    Assert(CurrentDatabase.TransactionManager.CommittedCount == 0,
      "private identity must not commit a transaction");
  }

  private static async Task ExecuteResultSerializationAsync()
  {
    ResetEnvironment();
    RoslynExecutor.Handler = (_, _) => Task.FromResult<object?>(new
    {
      DisplayName = "serialized",
      Values = new List<int> { 1, 2, 3 },
    });

    var success = JsonNode.Parse(
      await SendAsync(CreateRequest("serialized-result", readOnly: true))
    )?.AsObject();
    Assert(success?["result"]?["displayName"]?.GetValue<string>() == "serialized",
      "anonymous execute result must use camel-case JSON properties");
    Assert(success?["result"]?["values"]?.AsArray().Count == 3,
      "nested execute result list must be preserved");

    ResetEnvironment();
    RoslynExecutor.Handler = (_, _) => Task.FromResult<object?>(new object());
    AssertError(
      await SendAsync(CreateGuardedWriteRequest("unsupported-result", "unsupported-result")),
      "CIVIL3D.RESULT_SERIALIZATION_FAILED"
    );
    Assert(RoslynExecutor.CallCount == 1, "script must run before result serialization fails");
    Assert(CurrentDocument.LockCount == 1, "failed result serialization must acquire one lock");
    Assert(CurrentDocument.ActiveLockCount == 0, "failed result serialization must release the lock");
    Assert(CurrentDatabase.TransactionManager.StartedCount == 1,
      "failed result serialization must start one transaction");
    Assert(CurrentDatabase.TransactionManager.CommittedCount == 0,
      "failed result serialization must not commit the write transaction");
    Assert(CurrentDatabase.TransactionManager.ActiveCount == 0,
      "failed result serialization must dispose the transaction");
  }

  private static async Task DirectGuardContractAsync()
  {
    ResetEnvironment();
    AssertError(
      await SendAsync(CreateRequest("missing-write", readOnly: false)),
      "CIVIL3D.DRAWING_GUARD_REQUIRED"
    );
    AssertNoCivilResourcesOpened();

    var malformedGuards = new JsonNode?[]
    {
      null,
      JsonValue.Create("not-an-object"),
      new JsonObject { ["databaseFilename"] = ActualPath },
      new JsonObject
      {
        ["databaseFilename"] = @"relative\Target.dwg",
        ["fingerprintGuid"] = ActualFingerprint.ToString("D"),
      },
      new JsonObject
      {
        ["databaseFilename"] = ActualPath,
        ["fingerprintGuid"] = "not-a-guid",
      },
      new JsonObject
      {
        ["databaseFilename"] = ActualPath,
        ["fingerprintGuid"] = ActualFingerprint.ToString("D"),
        ["unexpected"] = true,
      },
    };

    for (var index = 0; index < malformedGuards.Length; index++)
    {
      ResetEnvironment();
      AssertError(
        await SendAsync(CreateRequest(
          $"invalid-{index}",
          readOnly: false,
          includeExpectedDrawing: true,
          expectedDrawing: malformedGuards[index]
        )),
        "CIVIL3D.DRAWING_GUARD_INVALID"
      );
      AssertNoCivilResourcesOpened();
    }

    ResetEnvironment();
    AssertResult(await SendAsync(CreateRequest("query-bootstrap", readOnly: true)), "ok");
    Assert(CurrentDocument.LockCount == 1, "unguarded query must remain bootstrap-capable");
    Assert(CurrentDatabase.TransactionManager.StartedCount == 1, "bootstrap query must open one transaction");
    Assert(CurrentDatabase.TransactionManager.CommittedCount == 0, "query transaction must not commit");
    Assert(RoslynExecutor.CallCount == 1, "bootstrap query must reach Roslyn");

    ResetEnvironment();
    AssertError(
      await SendAsync(CreateRequest(
        "query-invalid-guard",
        readOnly: true,
        includeExpectedDrawing: true,
        expectedDrawing: new JsonObject
        {
          ["databaseFilename"] = ActualPath,
          ["fingerprintGuid"] = 7,
        }
      )),
      "CIVIL3D.DRAWING_GUARD_INVALID"
    );
    AssertNoCivilResourcesOpened();
  }

  private static async Task DrawingIdentityOrderingAsync()
  {
    ResetEnvironment(actualPath: @"c:\projects\plans\target.dwg");
    var normalizedCaseVariant = Guard(
      @"C:\Projects\Plans\Archive\..\Target.dwg",
      ActualFingerprint
    );
    AssertResult(
      await SendAsync(CreateRequest(
        "matching-write",
        readOnly: false,
        includeExpectedDrawing: true,
        expectedDrawing: normalizedCaseVariant
      )),
      "ok"
    );
    Assert(CurrentDocument.LockCount == 1, "matching guard must allow the document lock");
    Assert(CurrentDatabase.TransactionManager.StartedCount == 1, "matching guard must allow the transaction");
    Assert(CurrentDatabase.TransactionManager.CommittedCount == 1, "matching write must commit");
    Assert(RoslynExecutor.CallCount == 1, "matching guard must allow Roslyn");

    ResetEnvironment();
    AssertError(
      await SendAsync(CreateRequest(
        "wrong-path",
        readOnly: false,
        includeExpectedDrawing: true,
        expectedDrawing: Guard(@"C:\Projects\Plans\Other.dwg", ActualFingerprint)
      )),
      "CIVIL3D.DRAWING_MISMATCH"
    );
    AssertNoCivilResourcesOpened();

    ResetEnvironment();
    AssertError(
      await SendAsync(CreateRequest(
        "wrong-fingerprint",
        readOnly: false,
        includeExpectedDrawing: true,
        expectedDrawing: Guard(ActualPath, Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"))
      )),
      "CIVIL3D.DRAWING_MISMATCH"
    );
    AssertNoCivilResourcesOpened();

    ResetEnvironment();
    AssertError(
      await SendAsync(CreateRequest(
        "guarded-query-mismatch",
        readOnly: true,
        includeExpectedDrawing: true,
        expectedDrawing: Guard(ActualPath, Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"))
      )),
      "CIVIL3D.DRAWING_MISMATCH"
    );
    AssertNoCivilResourcesOpened();

    ResetEnvironment(actualPath: string.Empty);
    AssertResult(
      await SendAsync(CreateRequest(
        "unsaved-match",
        readOnly: true,
        includeExpectedDrawing: true,
        expectedDrawing: Guard(string.Empty, ActualFingerprint)
      )),
      "ok"
    );

    ResetEnvironment();
    AssertError(
      await SendAsync(CreateRequest(
        "empty-only-matches-empty",
        readOnly: true,
        includeExpectedDrawing: true,
        expectedDrawing: Guard(string.Empty, ActualFingerprint)
      )),
      "CIVIL3D.DRAWING_MISMATCH"
    );
    AssertNoCivilResourcesOpened();
  }

  private static async Task SerializedDispatchStatusAsync()
  {
    ResetEnvironment();
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    RoslynExecutor.Handler = async (code, _) =>
    {
      if (code == "first") await releaseFirst.Task;
      return code;
    };

    var first = Task.Run(() => SendAsync(CreateGuardedWriteRequest("parallel-first", "first")));
    await WaitUntilAsync(() => RoslynExecutor.CallCount == 1);

    var waiters = Enumerable.Range(2, 3)
      .Select(index => Task.Run(() => SendAsync(CreateGuardedWriteRequest(
        $"parallel-{index}",
        $"waiter-{index}"
      ))))
      .ToArray();
    await WaitUntilAsync(() => PluginRuntime.GetStatus().QueueDepth == waiters.Length);

    var heldStatus = PluginRuntime.GetStatus();
    Assert(heldStatus.OperationInProgress, "first operation must be active");
    Assert(heldStatus.CurrentOperation == "executeCode", "active method must be reported");
    Assert(heldStatus.QueueDepth == waiters.Length, "all waiting operations must be counted exactly");
    Assert(RoslynExecutor.CallCount == 1, "waiting operations must not reach Roslyn");
    Assert(CurrentDocument.LockCount == 1, "waiters must not acquire document locks");
    Assert(CurrentDatabase.TransactionManager.StartedCount == 1, "waiters must not open transactions");

    releaseFirst.SetResult();
    AssertResult(await first, "first");
    for (var index = 0; index < waiters.Length; index++)
    {
      AssertResult(await waiters[index], $"waiter-{index + 2}");
    }
    Assert(RoslynExecutor.MaxActiveCount == 1, "maxConcurrency must remain one");
    AssertCleanStatus();
  }

  private static async Task NativeExecutionResultBridgeAsync()
  {
    ResetEnvironment();
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    AssertResult(
      await SendAsync(CreateGuardedWriteRequest("execution-result-complete-before-await", "complete-before-await")),
      "complete-before-await"
    );
    Assert(RoslynExecutor.CallCount == 1,
      "an already-complete native result must run its callback exactly once");
    Assert(CurrentDocument.LockCount == 1,
      "an already-complete native result must take one document lock");
    Assert(CurrentDatabase.TransactionManager.StartedCount == 1,
      "an already-complete native result must start one transaction");
    AssertCleanStatus();

    ResetEnvironment();
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    Application.DocumentManager.CompleteDuringOnCompletedRegistration = true;
    AssertResult(
      await SendAsync(
        CreateGuardedWriteRequest("execution-result-registration-race", "registration-race")
      ).WaitAsync(TimeSpan.FromSeconds(1)),
      "registration-race"
    );
    Assert(RoslynExecutor.CallCount == 1,
      "completion during OnCompleted registration must still run the callback exactly once");
    Assert(CurrentDocument.LockCount == 1,
      "completion during OnCompleted registration must take one document lock");
    Assert(CurrentDatabase.TransactionManager.StartedCount == 1,
      "completion during OnCompleted registration must start one transaction");
    AssertCleanStatus();
  }

  private static async Task CommandContextAdmissionTimeoutAsync()
  {
    ResetEnvironment();
    CommandContextAdmission.StartDeadlineOverrideForTests = TimeSpan.FromMilliseconds(25);
    var commandContextQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseStaleCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var staleCallbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var lateFaultObserved = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
    Application.DocumentManager.BeforeCommandContextAsync = async () =>
    {
      commandContextQueued.TrySetResult();
      await releaseStaleCallback.Task;
    };
    CommandContextAdmission.LateFaultObserverForTests = exception =>
      lateFaultObserved.TrySetResult(exception);

    try
    {
      var timedOut = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
        "command-context-timeout",
        "must-not-run"
      )));
      await commandContextQueued.Task;
      AssertError(await timedOut, "CIVIL3D.COMMAND_CONTEXT_TIMEOUT");
      Assert(RoslynExecutor.CallCount == 0, "an unstarted command context must not enter Roslyn");
      Assert(CurrentDocument.LockCount == 0, "an unstarted command context must not take a document lock");
      Assert(CurrentDatabase.TransactionManager.StartedCount == 0,
        "an unstarted command context must not start a transaction");
      AssertCleanStatus();

      // The serialized gate is available before the stale native callback is
      // released. A later manual request can therefore run independently.
      Application.DocumentManager.BeforeCommandContextAsync = null;
      RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
      AssertResult(
        await SendAsync(CreateGuardedWriteRequest("after-command-context-timeout", "after-timeout")),
        "after-timeout"
      );
      Assert(RoslynExecutor.CallCount == 1,
        "the subsequent request must be the first and only script execution");

      Application.DocumentManager.AfterCommandContextAsync = () =>
      {
        staleCallbackFinished.TrySetResult();
        throw new InvalidOperationException("expected late command-context fault");
      };
      releaseStaleCallback.SetResult();
      await staleCallbackFinished.Task;
      var observedFault = await lateFaultObserved.Task;
      Assert(observedFault.ToString().Contains("expected late command-context fault", StringComparison.Ordinal),
        "a late scheduler fault after abandonment must be observed");
      Assert(RoslynExecutor.CallCount == 1,
        "the stale callback must return before Roslyn or caller code executes");
      Assert(CurrentDocument.LockCount == 1,
        "the stale callback must not acquire an additional document lock");
      Assert(CurrentDatabase.TransactionManager.StartedCount == 1,
        "the stale callback must not start an additional transaction");
      AssertCleanStatus();
    }
    finally
    {
      releaseStaleCallback.TrySetResult();
      Application.DocumentManager.BeforeCommandContextAsync = null;
      Application.DocumentManager.AfterCommandContextAsync = null;
      CommandContextAdmission.LateFaultObserverForTests = null;
      CommandContextAdmission.StartDeadlineOverrideForTests = null;
    }
  }

  private static async Task CommandContextAdmissionFaultsAsync()
  {
    ResetEnvironment();
    var releaseLateCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var lateCallbackReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Application.DocumentManager.CommandContextScheduleOverrideAsync = (callback, userData) =>
    {
      _ = Task.Run(async () =>
      {
        await releaseLateCallback.Task;
        await callback(userData);
        lateCallbackReturned.TrySetResult();
      });
      return Task.FromException(new InvalidOperationException("expected command-context scheduling failure"));
    };
    AssertError(
      await SendAsync(CreateGuardedWriteRequest("command-context-schedule-failure", "must-not-run")),
      "CIVIL3D.TRANSACTION_FAILED"
    );
    Assert(RoslynExecutor.CallCount == 0, "a scheduler failure must propagate before Roslyn");
    AssertNoCivilResourcesOpened();
    AssertCleanStatus();

    releaseLateCallback.SetResult();
    await lateCallbackReturned.Task;
    Assert(RoslynExecutor.CallCount == 0,
      "a callback arriving after scheduler failure must be abandoned before Roslyn");
    AssertNoCivilResourcesOpened();
    AssertCleanStatus();
  }

  private static async Task WaitingForCommandContextProgressAsync()
  {
    ResetEnvironment();
    var enteredBeforeCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseBeforeCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<string>? execution = null;
    Application.DocumentManager.BeforeCommandContextAsync = async () =>
    {
      enteredBeforeCallback.TrySetResult();
      await releaseBeforeCallback.Task;
    };

    try
    {
      execution = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
        "waiting-command-context",
        "waiting-command-context"
      )));
      await enteredBeforeCallback.Task;

      AssertActiveHealth(
        await GetHealthAsync(),
        "WaitingForCommandContext",
        "health must report an operation queued before the AutoCAD callback starts"
      );
      Assert(RoslynExecutor.CallCount == 0,
        "the script must not run before the AutoCAD command-context callback starts");
      Assert(CurrentDocument.ActiveLockCount == 0,
        "the document lock must not be acquired before the command-context callback starts");
    }
    finally
    {
      releaseBeforeCallback.TrySetResult();
      if (execution != null) await execution;
      Application.DocumentManager.BeforeCommandContextAsync = null;
    }

    AssertIdleHealth(await GetHealthAsync(), "completed command-context operation must return to idle");
  }

  private static async Task WaitingForCommandContextCompletionProgressAsync()
  {
    ResetEnvironment();
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    var afterCallbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseOuterCommandContextTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var afterCallbackVisits = 0;
    Task<string>? first = null;
    Task<string>? second = null;
    Application.DocumentManager.AfterCommandContextAsync = async () =>
    {
      if (Interlocked.Increment(ref afterCallbackVisits) == 1)
      {
        afterCallbackEntered.TrySetResult();
        await releaseOuterCommandContextTask.Task;
      }
    };

    try
    {
      first = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
        "outer-completion-first",
        "outer-completion-first"
      )));
      await afterCallbackEntered.Task;

      AssertActiveHealth(
        await GetHealthAsync(),
        "WaitingForCommandContextCompletion",
        "health must keep the active lease until AutoCAD completes the outer command-context task"
      );
      Assert(RoslynExecutor.CallCount == 1,
        "the first callback must have completed its script before the outer task is held");

      second = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
        "outer-completion-second",
        "outer-completion-second"
      )));
      await WaitUntilAsync(() => PluginRuntime.GetStatus().QueueDepth == 1);
      Assert(RoslynExecutor.CallCount == 1,
        "a second operation must not enter while the first AutoCAD outer task is still pending");
    }
    finally
    {
      releaseOuterCommandContextTask.TrySetResult();
      if (first != null) AssertResult(await first, "outer-completion-first");
      if (second != null) AssertResult(await second, "outer-completion-second");
      Application.DocumentManager.AfterCommandContextAsync = null;
    }

    AssertIdleHealth(await GetHealthAsync(), "completed outer command-context task must return to idle");
  }

  private static async Task RunningScriptProgressAndCancellationAsync()
  {
    ResetEnvironment();
    CommandContextAdmission.StartDeadlineOverrideForTests = TimeSpan.Zero;
    var releaseScript = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    RoslynExecutor.Handler = async (code, _) =>
    {
      if (code == "hold-running-script") await releaseScript.Task;
      return code;
    };

    using var activeCancellation = new CancellationTokenSource();
    var first = Task.Run(() => SendAsync(
      CreateGuardedWriteRequest("running-script-first", "hold-running-script"),
      activeCancellation.Token
    ));
    await WaitForHealthStageAsync("RunningScript");

    var second = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
      "running-script-second",
      "running-script-second"
    )));
    await WaitUntilAsync(() => PluginRuntime.GetStatus().QueueDepth == 1);
    activeCancellation.Cancel();

    Assert(!first.IsCompleted,
      "cancelling a caller token after execution starts must not release the running Civil operation");
    AssertActiveHealth(
      await GetHealthAsync(),
      "RunningScript",
      "caller cancellation must not clear the active running-script stage"
    );
    Assert(RoslynExecutor.CallCount == 1,
      "a queued operation must not overlap the still-running script after caller cancellation");

    releaseScript.SetResult();
    AssertResult(await first, "hold-running-script");
    AssertResult(await second, "running-script-second");
    AssertIdleHealth(await GetHealthAsync(), "released running script must return to idle");
  }

  private static async Task ProgressCleanupAsync()
  {
    ResetEnvironment();
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    AssertResult(
      await SendAsync(CreateGuardedWriteRequest("progress-success", "progress-success")),
      "progress-success"
    );
    AssertIdleHealth(await GetHealthAsync(), "successful execution must clear progress");

    ResetEnvironment();
    RoslynExecutor.Handler = (_, _) => throw new InvalidOperationException("expected progress failure");
    AssertError(
      await SendAsync(CreateGuardedWriteRequest("progress-error", "progress-error")),
      "CIVIL3D.TRANSACTION_FAILED"
    );
    AssertIdleHealth(await GetHealthAsync(), "failed execution must clear progress");
  }

  private static async Task SaveAfterCommitOrderingAsync()
  {
    ResetEnvironment();
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    var transactionCountAtSave = -1;
    var documentLockCountAtSave = -1;
    CurrentDatabase.BeforeSaveAs = () =>
    {
      transactionCountAtSave = CurrentDatabase.TransactionManager.ActiveCount;
      documentLockCountAtSave = CurrentDocument.ActiveLockCount;
    };

    AssertResult(
      await SendAsync(CreateGuardedWriteRequest("save-after-commit", "save-result", saveDrawing: true)),
      "save-result"
    );

    Assert(CurrentDatabase.TransactionManager.CommittedCount == 1,
      "save request must commit the write transaction exactly once");
    Assert(CurrentDatabase.SaveAsCallCount == 1,
      "save request must save the drawing exactly once");
    Assert(CurrentDatabase.LastSavedFilename == ActualPath,
      "save request must use the guarded active drawing path");
    Assert(transactionCountAtSave == 0,
      "save must start only after the script transaction is disposed");
    Assert(documentLockCountAtSave == 0,
      "save must start only after the script document lock is disposed");
  }

  private static async Task SaveRequestValidationAsync()
  {
    const string templatePath = @"C:\Users\test\AppData\Local\Autodesk\C3D 2025\Template\mt_2025.dwt";
    ResetEnvironment(actualPath: templatePath, dwgTitled: false);
    AssertError(
      await SendAsync(CreateRequest(
        "save-unnamed",
        readOnly: false,
        includeExpectedDrawing: true,
        expectedDrawing: Guard(templatePath, ActualFingerprint),
        code: "must-not-run",
        saveDrawing: true
      )),
      "CIVIL3D.SAVE_PATH_REQUIRED"
    );
    AssertNoCivilResourcesOpened();
    Assert(CurrentDatabase.SaveAsCallCount == 0,
      "unnamed drawing rejection must not attempt a save");

    ResetEnvironment();
    AssertError(
      await SendAsync(CreateRequest(
        "save-read-only",
        readOnly: true,
        code: "must-not-run",
        saveDrawing: true
      )),
      "CIVIL3D.INVALID_INPUT"
    );
    AssertNoCivilResourcesOpened();
  }

  private static async Task SaveFailureCompletesIdempotencyAsync()
  {
    ResetEnvironment();
    CurrentDatabase.SaveAsException = new InvalidOperationException("expected save failure");
    const string key = "write:save-failure";

    AssertError(
      await SendAsync(CreateIdempotentWriteRequest(
        "save-failure-first",
        key,
        "committed-before-save",
        saveDrawing: true
      )),
      "CIVIL3D.SAVE_FAILED"
    );
    Assert(CurrentDatabase.TransactionManager.CommittedCount == 1,
      "save failure must occur after the in-memory write committed");
    Assert(CurrentDatabase.SaveAsCallCount == 1,
      "failed save must be attempted exactly once");

    AssertError(
      await SendAsync(CreateIdempotentWriteRequest(
        "save-failure-duplicate",
        key,
        "committed-before-save",
        saveDrawing: true
      )),
      "CIVIL3D.IDEMPOTENCY_COMPLETED"
    );
    Assert(RoslynExecutor.CallCount == 1,
      "a save failure duplicate must not repeat the committed drawing modification");
    Assert(CurrentDatabase.SaveAsCallCount == 1,
      "a save failure duplicate must not repeat the save attempt implicitly");
  }

  private static async Task IdempotencyFirstSuccessAndCompletedDuplicateAsync()
  {
    ResetEnvironment();
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    const string key = "write:first-success";

    AssertResult(
      await SendAsync(CreateIdempotentWriteRequest("idempotency-first", key, "first-success")),
      "first-success"
    );
    Assert(CurrentDatabase.TransactionManager.CommittedCount == 1,
      "first idempotent write must commit exactly once");

    var normalizedDuplicate = JsonNode.Parse(
      CreateIdempotentWriteRequest("idempotency-completed", key, "first-success")
    )!.AsObject();
    normalizedDuplicate["params"]!.AsObject()["expectedDrawing"] = Guard(
      @"c:\projects\plans\archive\..\target.dwg",
      ActualFingerprint
    );
    AssertError(
      await SendAsync(normalizedDuplicate.ToJsonString()),
      "CIVIL3D.IDEMPOTENCY_COMPLETED"
    );
    AssertErrorDoesNotContain(
      await SendAsync(CreateIdempotentWriteRequest("idempotency-completed-message", key, "first-success")),
      key
    );
    Assert(RoslynExecutor.CallCount == 1, "completed duplicate must not reach Roslyn");
    Assert(CurrentDocument.LockCount == 1, "completed duplicate must not lock the drawing");
    Assert(CurrentDatabase.TransactionManager.StartedCount == 1,
      "completed duplicate must not start a transaction");
  }

  private static async Task IdempotencyInProgressDuplicateAsync()
  {
    ResetEnvironment();
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    RoslynExecutor.Handler = async (code, _) =>
    {
      if (code == "in-progress") await releaseFirst.Task;
      return code;
    };
    const string key = "write:in-progress";

    var first = Task.Run(() => SendAsync(CreateIdempotentWriteRequest(
      "idempotency-in-progress-first", key, "in-progress"
    )));
    await WaitUntilAsync(() => RoslynExecutor.CallCount == 1);

    AssertError(
      await SendAsync(CreateIdempotentWriteRequest(
        "idempotency-in-progress-duplicate", key, "in-progress"
      )),
      "CIVIL3D.IDEMPOTENCY_IN_PROGRESS"
    );
    Assert(RoslynExecutor.CallCount == 1, "in-progress duplicate must not reach Roslyn");
    Assert(PluginRuntime.GetStatus().QueueDepth == 0,
      "in-progress duplicate must not enter the serialized operation queue");
    Assert(CurrentDocument.LockCount == 1, "in-progress duplicate must not lock the drawing");
    Assert(CurrentDatabase.TransactionManager.StartedCount == 1,
      "in-progress duplicate must not start a transaction");

    releaseFirst.SetResult();
    AssertResult(await first, "in-progress");
  }

  private static async Task IdempotencyConflictAsync()
  {
    ResetEnvironment();
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    const string key = "write:binding-conflict";

    AssertResult(
      await SendAsync(CreateIdempotentWriteRequest("idempotency-conflict-first", key, "original")),
      "original"
    );
    AssertError(
      await SendAsync(CreateIdempotentWriteRequest("idempotency-conflict", key, "different-code")),
      "CIVIL3D.IDEMPOTENCY_CONFLICT"
    );
    var differentDrawing = JsonNode.Parse(
      CreateIdempotentWriteRequest("idempotency-drawing-conflict", key, "original")
    )!.AsObject();
    differentDrawing["params"]!.AsObject()["expectedDrawing"] = Guard(
      ActualPath,
      Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee")
    );
    AssertError(
      await SendAsync(differentDrawing.ToJsonString()),
      "CIVIL3D.IDEMPOTENCY_CONFLICT"
    );
    AssertError(
      await SendAsync(CreateIdempotentWriteRequest(
        "idempotency-save-conflict",
        key,
        "original",
        saveDrawing: true
      )),
      "CIVIL3D.IDEMPOTENCY_CONFLICT"
    );
    Assert(RoslynExecutor.CallCount == 1, "conflict must not reach Roslyn");
    Assert(CurrentDocument.LockCount == 1, "conflict must not lock the drawing");
    Assert(CurrentDatabase.TransactionManager.StartedCount == 1,
      "conflict must not start a transaction");
  }

  private static async Task IdempotencyFailureReleasesKeyAsync()
  {
    ResetEnvironment();
    const string key = "write:release-after-failure";
    RoslynExecutor.Handler = (code, _) => code == "will-fail"
      ? throw new InvalidOperationException("expected idempotency test failure")
      : Task.FromResult<object?>(code);

    AssertError(
      await SendAsync(CreateIdempotentWriteRequest("idempotency-failure", key, "will-fail")),
      "CIVIL3D.TRANSACTION_FAILED"
    );
    Assert(CurrentDatabase.TransactionManager.CommittedCount == 0,
      "failed write must not commit before releasing its key");

    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    AssertResult(
      await SendAsync(CreateIdempotentWriteRequest("idempotency-retry", key, "will-fail")),
      "will-fail"
    );
    Assert(RoslynExecutor.CallCount == 2, "manual retry must reach Roslyn after a failure");
    Assert(CurrentDatabase.TransactionManager.CommittedCount == 1,
      "manual retry must be able to commit");
  }

  private static async Task IdempotencyCapacityEvictionAsync()
  {
    ResetEnvironment();
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    for (var index = 0; index <= IdempotencyRegistry.CompletedCapacity; index++)
    {
      AssertResult(
        await SendAsync(CreateIdempotentWriteRequest(
          $"idempotency-capacity-{index}",
          $"write:capacity:{index}",
          $"capacity-{index}"
        )),
        $"capacity-{index}"
      );
    }

    AssertError(
      await SendAsync(CreateIdempotentWriteRequest(
        "idempotency-capacity-newest", "write:capacity:256", "capacity-256"
      )),
      "CIVIL3D.IDEMPOTENCY_COMPLETED"
    );
    AssertResult(
      await SendAsync(CreateIdempotentWriteRequest(
        "idempotency-capacity-oldest", "write:capacity:0", "capacity-0"
      )),
      "capacity-0"
    );
    Assert(RoslynExecutor.CallCount == IdempotencyRegistry.CompletedCapacity + 2,
      "only the oldest completed key must be evicted");
  }

  private static async Task IdempotencyInvalidKeyAsync()
  {
    var invalidKeys = new JsonNode?[]
    {
      JsonValue.Create(string.Empty),
      JsonValue.Create("contains space"),
      JsonValue.Create("invalid/slash"),
      JsonValue.Create(new string('a', 129)),
      JsonValue.Create(7),
      null,
    };

    for (var index = 0; index < invalidKeys.Length; index++)
    {
      ResetEnvironment();
      AssertError(
        await SendAsync(CreateIdempotentWriteRequest(
          $"idempotency-invalid-{index}", invalidKeys[index], "invalid-key"
        )),
        "CIVIL3D.INVALID_INPUT"
      );
      AssertNoCivilResourcesOpened();
    }
  }

  private static Task FailedListenerStartDoesNotReportRunningAsync()
  {
    ResetEnvironment();
    RpcTcpServer.ThrowOnStart = true;
    try
    {
      var threw = false;
      try
      {
        PluginRuntime.StartServer();
      }
      catch (InvalidOperationException ex) when (ex.Message == "expected listener start failure")
      {
        threw = true;
      }

      Assert(threw, "listener start fixture must throw");
      Assert(!PluginRuntime.GetStatus().IsRunning,
        "failed listener start must not report the listener as running");

      RpcTcpServer.ThrowOnStart = false;
      PluginRuntime.StartServer();
      Assert(PluginRuntime.GetStatus().IsRunning,
        "listener must remain startable after a failed start");
    }
    finally
    {
      RpcTcpServer.ThrowOnStart = false;
      PluginRuntime.StopServer();
    }

    return Task.CompletedTask;
  }

  private static Task EndpointRegistrationLifecycleAsync()
  {
    ResetEnvironment();
    RpcTcpServer.ThrowAddressInUseOnDefaultPort = true;
    try
    {
      PluginRuntime.StartServer();
      var status = PluginRuntime.GetStatus();
      Assert(status.IsRunning, "fallback listener must report running");
      Assert(status.Port == 48123, "address-in-use fallback must publish the bound port");

      var files = Directory.GetFiles(EndpointDirectory, "*.json");
      Assert(files.Length == 1, "one running plugin session must publish one endpoint record");
      var record = JsonNode.Parse(File.ReadAllText(files[0]))?.AsObject();
      Assert(record?["schemaVersion"]?.GetValue<int>() == EndpointRegistration.SchemaVersion,
        "endpoint record must use the supported schema");
      Assert(record?["instanceId"]?.GetValue<string>() == PluginRuntime.InstanceId,
        "endpoint record must identify the plugin session");
      Assert(record?["processId"]?.GetValue<int>() == Environment.ProcessId,
        "endpoint record must identify the Civil process");
      Assert(record?["port"]?.GetValue<int>() == status.Port,
        "endpoint record must publish the actual fallback port");

      PluginRuntime.StopServer();
      Assert(Directory.GetFiles(EndpointDirectory, "*.json").Length == 0,
        "normal plugin stop must remove its endpoint record");
    }
    finally
    {
      RpcTcpServer.ThrowAddressInUseOnDefaultPort = false;
      PluginRuntime.StopServer();
    }

    return Task.CompletedTask;
  }

  private static async Task HealthBypassesSerializedQueueAsync()
  {
    ResetEnvironment();
    PluginRuntime.StartServer();
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<string>? first = null;
    Task<string>? waiter = null;

    try
    {
      RoslynExecutor.Handler = async (code, _) =>
      {
        if (code == "health-holder") await releaseFirst.Task;
        return code;
      };

      first = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
        "health-active",
        "health-holder"
      )));
      await WaitUntilAsync(() => RoslynExecutor.CallCount == 1);

      waiter = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
        "health-waiter",
        "health-waiter"
      )));
      await WaitUntilAsync(() => PluginRuntime.GetStatus().QueueDepth == 1);

      var statusBefore = PluginRuntime.GetStatus();
      var commandContextsBefore = Application.DocumentManager.CommandContextCallCount;
      var locksBefore = CurrentDocument.LockCount;
      var transactionsBefore = CurrentDatabase.TransactionManager.StartedCount;

      var healthTask = SendAsync(CreateHealthRequest("health-status"));
      var completed = await Task.WhenAny(healthTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
      Assert(ReferenceEquals(completed, healthTask),
        "health must complete while the active Civil operation remains blocked");

      var response = JsonNode.Parse(await healthTask)?.AsObject();
      var health = response?["result"]?.AsObject();
      Assert(health != null, "health response must contain a result object");
      var expectedFields = new HashSet<string>
      {
        "connected",
        "listenerRunning",
        "operationInProgress",
        "currentOperation",
        "operationStage",
        "operationElapsedMs",
        "stageElapsedMs",
        "queueDepth",
        "instanceId",
        "processId",
        "port",
        "startedAtUtc",
        "mode",
        "roslyn",
      };
      Assert(health!.Count == expectedFields.Count && health.All(item => expectedFields.Contains(item.Key)),
        "health response must remain limited to the stable status fields");
      Assert(health["connected"]?.GetValue<bool>() == true, "health must report the live connection");
      Assert(health["listenerRunning"]?.GetValue<bool>() == true, "health must report the listener");
      Assert(health["operationInProgress"]?.GetValue<bool>() == true,
        "health must report the active operation");
      Assert(health["currentOperation"]?.GetValue<string>() == "executeCode",
        "health must identify the active method");
      Assert(health["operationStage"]?.GetValue<string>() == "RunningScript",
        "health must identify the active execution stage without exposing caller data");
      Assert(health["operationElapsedMs"]?.GetValue<double>() >= 0,
        "health must report a nonnegative active-operation elapsed time");
      Assert(health["stageElapsedMs"]?.GetValue<double>() >= 0,
        "health must report a nonnegative active-stage elapsed time");
      Assert(health["queueDepth"]?.GetValue<int>() == 1,
        "health must report the waiting executeCode request only");
      Assert(health["instanceId"]?.GetValue<string>() == PluginRuntime.InstanceId,
        "health must identify the current plugin session");
      Assert(health["processId"]?.GetValue<int>() == Environment.ProcessId,
        "health must identify the Civil process without touching the drawing");
      Assert(health["port"]?.GetValue<int>() == PluginRuntime.Port,
        "health must report the actual listener port");
      Assert(health["startedAtUtc"] != null,
        "health must report the plugin-session start time");
      Assert(health["mode"]?.GetValue<string>() == "code_execution", "health mode must remain stable");
      Assert(health["roslyn"]?.GetValue<bool>() == true, "health must report Roslyn mode");
      Assert(!health.ContainsKey("drawingName"), "health must not inspect or guess drawing identity");

      var statusAfter = PluginRuntime.GetStatus();
      Assert(statusBefore.QueueDepth == 1 && statusAfter.QueueDepth == 1,
        "health itself must not increase queue depth");
      Assert(statusAfter.OperationInProgress, "active operation must remain active during health");
      Assert(RoslynExecutor.CallCount == 1, "health and waiter must not reach Roslyn");
      Assert(Application.DocumentManager.CommandContextCallCount == commandContextsBefore,
        "health must not enter the Civil command context");
      Assert(CurrentDocument.LockCount == locksBefore, "health must not take a document lock");
      Assert(CurrentDatabase.TransactionManager.StartedCount == transactionsBefore,
        "health must not start a transaction");
    }
    finally
    {
      releaseFirst.TrySetResult();
      if (first != null) AssertResult(await first, "health-holder");
      if (waiter != null) AssertResult(await waiter, "health-waiter");
      PluginRuntime.StopServer();
    }

    AssertCleanStatus();
  }

  private static async Task ApiLookupBypassesSerializedQueueAsync()
  {
    ResetEnvironment();
    PluginRuntime.StartServer();
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<string>? first = null;
    Task<string>? waiter = null;
    var fixtureAssembly = CreateApiLookupFixtureAssembly();
    ApiLookup.LoadedAssembliesProvider = () => new[] { fixtureAssembly };

    try
    {
      RoslynExecutor.Handler = async (code, _) =>
      {
        if (code == "api-lookup-holder") await releaseFirst.Task;
        return code;
      };

      first = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
        "api-lookup-active",
        "api-lookup-holder"
      )));
      await WaitUntilAsync(() => RoslynExecutor.CallCount == 1);

      waiter = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
        "api-lookup-waiter",
        "api-lookup-waiter"
      )));
      await WaitUntilAsync(() => PluginRuntime.GetStatus().QueueDepth == 1);

      var statusBefore = PluginRuntime.GetStatus();
      var commandContextsBefore = Application.DocumentManager.CommandContextCallCount;
      var locksBefore = CurrentDocument.LockCount;
      var transactionsBefore = CurrentDatabase.TransactionManager.StartedCount;

      var lookupTask = SendAsync(CreateApiLookupRequest("api-lookup", "TinSurface AddVertex", limit: 1));
      var completed = await Task.WhenAny(lookupTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
      Assert(ReferenceEquals(completed, lookupTask),
        "api lookup must complete while the active Civil operation remains blocked");

      var response = JsonNode.Parse(await lookupTask)?.AsObject();
      var result = response?["result"]?.AsObject();
      Assert(result != null, "api lookup response must contain a result object");
      Assert(result!["total"]?.GetValue<int>() == 1, "token AND search must find only the matching member");
      Assert(result["returned"]?.GetValue<int>() == 1, "api lookup must honor the result limit");
      Assert(result["truncated"]?.GetValue<bool>() == false, "single matching result must not be truncated");
      var item = result["results"]?.AsArray().Single()?.AsObject();
      Assert(item?["assembly"]?.GetValue<string>() == "AeccDbMgd", "assembly matching must be case-preserving output");
      Assert(item?["type"]?.GetValue<string>() == "Fixture.Api.TinSurfaceFixture", "type metadata must be returned");
      Assert(item?["member"]?.GetValue<string>() == "void AddVertex(string vertex)",
        "member signature must include a code-useful return type and named parameter without invocation");

      var propertyResponse = JsonNode.Parse(
        await SendAsync(CreateApiLookupRequest("property-metadata", "Forbidden"))
      )?.AsObject();
      Assert(propertyResponse?["result"]?["results"]?.AsArray().Single()?.AsObject()?["member"]?.GetValue<string>() == "string Forbidden { get; }",
        "property metadata must expose its public accessors without invoking its throwing getter");

      var constructorResponse = JsonNode.Parse(
        await SendAsync(CreateApiLookupRequest("constructor-metadata", "label"))
      )?.AsObject();
      Assert(constructorResponse?["result"]?["results"]?.AsArray().Single()?.AsObject()?["member"]?.GetValue<string>() == "TinSurfaceFixture(string label)",
        "public constructors must be included with useful named parameter signatures");

      var genericResponse = JsonNode.Parse(
        await SendAsync(CreateApiLookupRequest("generic-metadata", "Project"))
      )?.AsObject();
      Assert(genericResponse?["result"]?["results"]?.AsArray().Single()?.AsObject()?["member"]?.GetValue<string>() == "static void Project<T>(ref int index, out string message)",
        "methods must include static state, generic parameters, and ref/out named parameter signatures");

      var limitedResponse = JsonNode.Parse(
        await SendAsync(CreateApiLookupRequest("limited", "TinSurface", limit: 1))
      )?.AsObject()?["result"]?.AsObject();
      var repeatedLimitedResponse = JsonNode.Parse(
        await SendAsync(CreateApiLookupRequest("limited-repeat", "TinSurface", limit: 1))
      )?.AsObject()?["result"]?.AsObject();
      Assert(limitedResponse?["total"]?.GetValue<int>() == 5,
        "type and public member metadata must both participate in search");
      Assert(limitedResponse?["returned"]?.GetValue<int>() == 1 &&
        limitedResponse?["truncated"]?.GetValue<bool>() == true,
        "api lookup limit must bound results and report truncation");
      Assert(limitedResponse?.ToJsonString() == repeatedLimitedResponse?.ToJsonString(),
        "api lookup ordering must be deterministic");

      var namespaceResponse = JsonNode.Parse(
        await SendAsync(CreateApiLookupRequest("namespace-filter", "TinSurface", @namespace: "fixture.api"))
      )?.AsObject();
      Assert(namespaceResponse?["result"]?["total"]?.GetValue<int>() > 0,
        "namespace filtering must be case-insensitive");

      var assemblyResponse = JsonNode.Parse(
        await SendAsync(CreateApiLookupRequest("assembly-filter", "TinSurface", assembly: "aeccdbmgd"))
      )?.AsObject();
      Assert(assemblyResponse?["result"]?["total"]?.GetValue<int>() > 0,
        "assembly filtering must be case-insensitive");

      AssertError(
        await SendAsync(CreateApiLookupRequest("unknown-assembly", "TinSurface", assembly: "not-allowed")),
        "CIVIL3D.INVALID_INPUT"
      );
      AssertError(await SendAsync(CreateApiLookupRequest("missing-query", null)), "CIVIL3D.INVALID_INPUT");
      AssertError(await SendAsync(CreateApiLookupRequest("invalid-limit", "TinSurface", limit: 51)), "CIVIL3D.INVALID_INPUT");

      var statusAfter = PluginRuntime.GetStatus();
      Assert(statusBefore.QueueDepth == 1 && statusAfter.QueueDepth == 1,
        "api lookup itself must not increase queue depth");
      Assert(statusAfter.OperationInProgress, "active operation must remain active during api lookup");
      Assert(RoslynExecutor.CallCount == 1, "api lookup and waiter must not reach Roslyn");
      Assert(Application.DocumentManager.CommandContextCallCount == commandContextsBefore,
        "api lookup must not enter the Civil command context");
      Assert(CurrentDocument.LockCount == locksBefore, "api lookup must not take a document lock");
      Assert(CurrentDatabase.TransactionManager.StartedCount == transactionsBefore,
        "api lookup must not start a transaction");

      ApiLookup.LoadedAssembliesProvider = () => Array.Empty<Assembly>();
      AssertError(
        await SendAsync(CreateApiLookupRequest("invalid-before-unavailable", "TinSurface", assembly: "not-allowed")),
        "CIVIL3D.INVALID_INPUT"
      );
      AssertError(await SendAsync(CreateApiLookupRequest("unavailable", "TinSurface")), "CIVIL3D.API_LOOKUP_UNAVAILABLE");
    }
    finally
    {
      ApiLookup.LoadedAssembliesProvider = null;
      releaseFirst.TrySetResult();
      if (first != null) AssertResult(await first, "api-lookup-holder");
      if (waiter != null) AssertResult(await waiter, "api-lookup-waiter");
      PluginRuntime.StopServer();
    }

    AssertCleanStatus();
  }

  private static async Task CancellationAndErrorCleanupAsync()
  {
    ResetEnvironment();
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    RoslynExecutor.Handler = async (code, _) =>
    {
      if (code == "hold") await releaseFirst.Task;
      if (code == "throw") throw new InvalidOperationException("expected test failure");
      return code;
    };

    var first = Task.Run(() => SendAsync(CreateGuardedWriteRequest("cancel-first", "hold")));
    await WaitUntilAsync(() => RoslynExecutor.CallCount == 1);

    using var cancellation = new CancellationTokenSource();
    var cancelledWaiter = Task.Run(() => SendAsync(
      CreateGuardedWriteRequest("cancel-waiter", "cancelled"),
      cancellation.Token
    ));
    await WaitUntilAsync(() => PluginRuntime.GetStatus().QueueDepth == 1);
    cancellation.Cancel();

    await AssertCancelledAsync(cancelledWaiter);
    var afterCancellation = PluginRuntime.GetStatus();
    Assert(afterCancellation.QueueDepth == 0, "cancelled waiter must leave the queue count");
    Assert(afterCancellation.OperationInProgress, "active holder must remain accurately active");

    releaseFirst.SetResult();
    AssertResult(await first, "hold");
    AssertCleanStatus();

    AssertError(
      await SendAsync(CreateGuardedWriteRequest("throwing-operation", "throw")),
      "CIVIL3D.TRANSACTION_FAILED"
    );
    AssertCleanStatus();
    Assert(CurrentDocument.ActiveLockCount == 0, "error must dispose the document lock");
    Assert(CurrentDatabase.TransactionManager.ActiveCount == 0, "error must dispose the transaction");

    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    AssertResult(
      await SendAsync(CreateGuardedWriteRequest("after-error", "after-error")),
      "after-error"
    );
    AssertCleanStatus();
  }

  private static async Task GateWaitMeasurementAsync()
  {
    ResetEnvironment();
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    RoslynExecutor.Handler = async (code, _) =>
    {
      if (code == "measurement-holder") await releaseFirst.Task;
      return code;
    };

    var first = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
      "measurement-first",
      "measurement-holder"
    )));
    await WaitUntilAsync(() => RoslynExecutor.CallCount == 1);

    var measuredRequest = CreateGuardedWriteRequest("measurement-second", "measurement-waiter");
    var parsed = JsonNode.Parse(measuredRequest)!.AsObject();
    parsed["params"]!.AsObject()[InternalBenchmarkMeasurement.ParameterName] = new JsonObject
    {
      ["schemaVersion"] = InternalBenchmarkMeasurement.SchemaVersion,
      ["correlationId"] = "run-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    };

    var second = Task.Run(() => SendAsync(parsed.ToJsonString()));
    await WaitUntilAsync(() => PluginRuntime.GetStatus().QueueDepth == 1);
    await Task.Delay(100);
    releaseFirst.SetResult();

    await first;
    var response = JsonNode.Parse(await second)!.AsObject();
    var measuredWait = response[InternalBenchmarkMeasurement.ResponsePropertyName]?
      ["commandContextWaitMs"]?.GetValue<double>();
    Assert(measuredWait >= 50, "serialized gate wait must remain in commandContextWaitMs");
    AssertCleanStatus();
  }

  private static Database CurrentDatabase => CurrentDocument.Database;
  private static Document CurrentDocument => Application.DocumentManager.MdiActiveDocument!;

  private static void ResetEnvironment(
    string actualPath = ActualPath,
    bool? dwgTitled = null)
  {
    Application.DocumentManager.BeforeCommandContextAsync = null;
    Application.DocumentManager.AfterCommandContextAsync = null;
    Application.DocumentManager.CommandContextScheduleException = null;
    Application.DocumentManager.CommandContextCompletionException = null;
    Application.DocumentManager.CommandContextScheduleOverrideAsync = null;
    Application.DocumentManager.CompleteDuringOnCompletedRegistration = false;
    CommandContextAdmission.StartDeadlineOverrideForTests = null;
    CommandContextAdmission.LateFaultObserverForTests = null;
    AssertCleanStatus();
    PluginRuntime.ResetIdempotencyForTests();
    var database = new Database
    {
      Filename = actualPath,
      FingerprintGuid = ActualFingerprint.ToString("B").ToUpperInvariant(),
    };
    Application.DocumentManager.MdiActiveDocument = new Document(database);
    Application.DocumentManager.CommandContextDelay = TimeSpan.Zero;
    Application.DwgTitled = (dwgTitled ?? !string.IsNullOrEmpty(actualPath)) ? 1 : 0;
    CivilApplication.ActiveDocument = new CivilDocument();
    RoslynExecutor.Reset();
  }

  private static JsonObject Guard(string databaseFilename, Guid fingerprintGuid)
    => new()
    {
      ["databaseFilename"] = databaseFilename,
      ["fingerprintGuid"] = fingerprintGuid.ToString("D"),
    };

  private static string CreateGuardedWriteRequest(
    string id,
    string code,
    bool saveDrawing = false)
    => CreateRequest(
      id,
      readOnly: false,
      includeExpectedDrawing: true,
      expectedDrawing: Guard(ActualPath, ActualFingerprint),
      code: code,
      saveDrawing: saveDrawing
    );

  private static string CreateIdempotentWriteRequest(
    string id,
    JsonNode? idempotencyKey,
    string code,
    bool saveDrawing = false)
  {
    var request = JsonNode.Parse(CreateGuardedWriteRequest(id, code, saveDrawing))!.AsObject();
    request["params"]!.AsObject()["idempotencyKey"] = idempotencyKey?.DeepClone();
    return request.ToJsonString();
  }

  private static string CreateIdempotentWriteRequest(
    string id,
    string idempotencyKey,
    string code,
    bool saveDrawing = false)
    => CreateIdempotentWriteRequest(
      id,
      JsonValue.Create(idempotencyKey),
      code,
      saveDrawing
    );

  private static string CreateHealthRequest(string id)
    => new JsonObject
    {
      ["jsonrpc"] = "2.0",
      ["method"] = "getCivil3DHealth",
      ["id"] = id,
    }.ToJsonString();

  private static string CreateApiLookupRequest(
    string id,
    string? query,
    string? assembly = null,
    string? @namespace = null,
    int? limit = null)
  {
    var parameters = new JsonObject();
    if (query != null) parameters["query"] = query;
    if (assembly != null) parameters["assembly"] = assembly;
    if (@namespace != null) parameters["namespace"] = @namespace;
    if (limit != null) parameters["limit"] = limit.Value;
    return new JsonObject
    {
      ["jsonrpc"] = "2.0",
      ["method"] = "apiLookup",
      ["params"] = parameters,
      ["id"] = id,
    }.ToJsonString();
  }

  private static Assembly CreateApiLookupFixtureAssembly()
  {
    var assembly = AssemblyBuilder.DefineDynamicAssembly(
      new AssemblyName("AeccDbMgd"),
      AssemblyBuilderAccess.Run
    );
    var module = assembly.DefineDynamicModule("FixtureApi");
    var type = module.DefineType("Fixture.Api.TinSurfaceFixture", TypeAttributes.Public | TypeAttributes.Class);
    var constructor = type.DefineConstructor(
      MethodAttributes.Public,
      CallingConventions.Standard,
      new[] { typeof(string) }
    );
    constructor.DefineParameter(1, ParameterAttributes.None, "label");
    constructor.GetILGenerator().Emit(OpCodes.Ret);

    var addVertex = type.DefineMethod(
      "AddVertex",
      MethodAttributes.Public,
      typeof(void),
      new[] { typeof(string) }
    );
    addVertex.DefineParameter(1, ParameterAttributes.None, "vertex");
    addVertex.GetILGenerator().Emit(OpCodes.Ret);

    var project = type.DefineMethod(
      "Project",
      MethodAttributes.Public | MethodAttributes.Static,
      CallingConventions.Standard,
      typeof(void),
      Type.EmptyTypes
    );
    project.DefineGenericParameters("T");
    project.SetParameters(typeof(int).MakeByRefType(), typeof(string).MakeByRefType());
    project.DefineParameter(1, ParameterAttributes.None, "index");
    project.DefineParameter(2, ParameterAttributes.Out, "message");
    project.GetILGenerator().Emit(OpCodes.Ret);

    var getter = type.DefineMethod(
      "get_Forbidden",
      MethodAttributes.Public | MethodAttributes.SpecialName,
      typeof(string),
      Type.EmptyTypes
    );
    var getterIl = getter.GetILGenerator();
    getterIl.Emit(OpCodes.Ldstr, "metadata lookup invoked a property getter");
    getterIl.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
    getterIl.Emit(OpCodes.Throw);
    var property = type.DefineProperty("Forbidden", PropertyAttributes.None, typeof(string), Type.EmptyTypes);
    property.SetGetMethod(getter);
    _ = type.CreateType();
    return assembly;
  }

  private static string CreateRequest(
    string id,
    bool readOnly,
    bool includeExpectedDrawing = false,
    JsonNode? expectedDrawing = null,
    string code = "return-value",
    bool saveDrawing = false)
  {
    var parameters = new JsonObject
    {
      ["code"] = code,
      ["readOnly"] = readOnly,
    };
    if (includeExpectedDrawing)
    {
      parameters["expectedDrawing"] = expectedDrawing?.DeepClone();
    }
    if (saveDrawing)
    {
      parameters["saveDrawing"] = true;
    }

    return new JsonObject
    {
      ["jsonrpc"] = "2.0",
      ["method"] = "executeCode",
      ["params"] = parameters,
      ["id"] = id,
    }.ToJsonString();
  }

  private static Task<string> SendAsync(
    string request,
    CancellationToken cancellationToken = default)
    => PluginRuntime.HandleRawRequestAsync(request, cancellationToken);

  private static void AssertNoCivilResourcesOpened()
  {
    Assert(CurrentDocument.LockCount == 0, "guard failure must precede document locking");
    Assert(CurrentDatabase.TransactionManager.StartedCount == 0, "guard failure must precede transactions");
    Assert(RoslynExecutor.CallCount == 0, "guard failure must precede Roslyn execution");
  }

  private static void AssertCleanStatus()
  {
    var status = PluginRuntime.GetStatus();
    Assert(!status.OperationInProgress, "operation status must be inactive");
    Assert(status.CurrentOperation == null, "current operation must be cleared");
    Assert(status.QueueDepth == 0, "waiting count must be zero");
  }

  private static async Task<JsonObject> GetHealthAsync()
  {
    var response = JsonNode.Parse(
      await SendAsync(CreateHealthRequest($"health-{Guid.NewGuid():N}"))
    )?.AsObject();
    var health = response?["result"]?.AsObject();
    Assert(health != null, "health must return a result object");
    return health!;
  }

  private static async Task WaitForHealthStageAsync(string expectedStage)
  {
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    while (true)
    {
      var health = await GetHealthAsync();
      if (health["operationStage"]?.GetValue<string>() == expectedStage) return;
      if (DateTime.UtcNow >= deadline)
      {
        throw new TimeoutException($"Timed out waiting for operation stage '{expectedStage}'.");
      }
      await Task.Delay(5);
    }
  }

  private static void AssertActiveHealth(
    JsonObject health,
    string expectedStage,
    string context)
  {
    Assert(health["operationInProgress"]?.GetValue<bool>() == true,
      $"{context}: health must remain responsive and report an active operation");
    Assert(health["currentOperation"]?.GetValue<string>() == "executeCode",
      $"{context}: health must report the active execute operation");
    Assert(health["operationStage"]?.GetValue<string>() == expectedStage,
      $"{context}: expected stage '{expectedStage}'");
    Assert(health["operationElapsedMs"]?.GetValue<double>() >= 0,
      $"{context}: operation elapsed milliseconds must be nonnegative");
    Assert(health["stageElapsedMs"]?.GetValue<double>() >= 0,
      $"{context}: stage elapsed milliseconds must be nonnegative");
  }

  private static void AssertIdleHealth(JsonObject health, string context)
  {
    Assert(health["operationInProgress"]?.GetValue<bool>() == false,
      $"{context}: health must report idle");
    Assert(health["currentOperation"] == null,
      $"{context}: idle current operation must be null");
    Assert(health["operationStage"] == null,
      $"{context}: idle operation stage must be null");
    Assert(health["operationElapsedMs"] == null,
      $"{context}: idle operation elapsed milliseconds must be null");
    Assert(health["stageElapsedMs"] == null,
      $"{context}: idle stage elapsed milliseconds must be null");
  }

  private static void AssertError(string responseText, string expectedCode)
  {
    var response = JsonNode.Parse(responseText)?.AsObject();
    Assert(response?["error"]?["code"]?.GetValue<string>() == expectedCode, $"expected {expectedCode}");
  }

  private static void AssertErrorDoesNotContain(string responseText, string forbiddenText)
  {
    var response = JsonNode.Parse(responseText)?.AsObject();
    var message = response?["error"]?["message"]?.GetValue<string>() ?? string.Empty;
    Assert(!message.Contains(forbiddenText, StringComparison.Ordinal),
      "idempotency error messages must not expose the caller key");
  }

  private static void AssertResult(string responseText, string expectedResult)
  {
    var response = JsonNode.Parse(responseText)?.AsObject();
    Assert(response?["result"]?.GetValue<string>() == expectedResult, $"expected result {expectedResult}");
  }

  private static async Task AssertCancelledAsync(Task<string> task)
  {
    try
    {
      await task;
      throw new InvalidOperationException("cancelled waiter unexpectedly completed");
    }
    catch (OperationCanceledException)
    {
      // Expected.
    }
  }

  private static async Task WaitUntilAsync(Func<bool> predicate)
  {
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    while (!predicate())
    {
      if (DateTime.UtcNow >= deadline)
      {
        throw new TimeoutException("Timed out waiting for deterministic test state.");
      }
      await Task.Delay(5);
    }
  }

  private static void Assert(bool condition, string message)
  {
    if (!condition) throw new InvalidOperationException(message);
  }
}
