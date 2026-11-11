using System.Text.Json.Nodes;
using System.Text.Json;
using System.Reflection;
using System.Reflection.Emit;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace Civil3DMcpPlugin;

internal static class PluginCoreTests
{
  private static ModalTestHost? _modalHost;
  private const string ActualPath = @"C:\Projects\Plans\Target.dwg";
  private static readonly Guid ActualFingerprint =
    Guid.Parse("11111111-2222-4333-8444-555555555555");
  private static readonly string EndpointDirectory = Path.Combine(
    Path.GetTempPath(),
    $"civil3d-mcp-plugin-tests-{Guid.NewGuid():N}"
  );
  // Journal and backup files of the tests never go to the real user folders.
  private static readonly string SafetyDirectory = Path.Combine(
    Path.GetTempPath(),
    $"civil3d-mcp-plugin-safety-tests-{Guid.NewGuid():N}"
  );
  private static readonly string JournalDirectory = Path.Combine(SafetyDirectory, "journal");
  private static readonly string BackupDirectory = Path.Combine(SafetyDirectory, "backups");

  public static async Task Main()
  {
    var previousEndpointDirectory = Environment.GetEnvironmentVariable(
      EndpointRegistration.DirectoryEnvironmentVariable
    );
    Environment.SetEnvironmentVariable(
      EndpointRegistration.DirectoryEnvironmentVariable,
      EndpointDirectory
    );
    Environment.SetEnvironmentVariable(WriteJournal.DirectoryEnvironmentVariable, JournalDirectory);
    Environment.SetEnvironmentVariable(DrawingBackup.DirectoryEnvironmentVariable, BackupDirectory);
    var tests = new (string Name, Func<Task> Run)[]
    {
      ("compiled modal command registration preserves implied selection", ModalCommandRegistrationAsync),
      ("direct execute guard is required and malformed guards fail closed", DirectGuardContractAsync),
      ("modal dispatch preserves transaction, save, and gate through own host end", ModalDispatchAsync),
      ("modal admission rejects stale tokens, wrong documents, and unstarted work", ModalAdmissionSafetyAsync),
      ("drawing identity is checked before lock, transaction, and Roslyn", DrawingIdentityOrderingAsync),
      ("private drawing identity is read without Roslyn or a commit", PrivateDrawingIdentityAsync),
      ("save runs after commit and releases transaction resources", SaveAfterCommitOrderingAsync),
      ("save rejects unsupported request states before Roslyn", SaveRequestValidationAsync),
      ("save into a read-only drawing is refused before the script runs", ReadOnlyDrawingSaveAsync),
      ("requireClean refuses a drawing with unsaved changes before the script runs", RequireCleanAsync),
      ("backup copies the saved drawing first and fails closed", BackupBeforeWriteAsync),
      ("a write returns a receipt next to its result and a query does not", WriteReceiptAsync),
      ("every write request leaves one journal line without code or result", WriteJournalAsync),
      ("the description is shown on the command line as plain text", DescriptionEchoAsync),
      ("health reports the cached active drawing and the build without a command", HealthActiveDrawingAsync),
      ("script time limit parameter is validated and forwarded", ScriptTimeLimitParameterAsync),
      ("save failure preserves committed idempotency state", SaveFailureCompletesIdempotencyAsync),
      ("failed listener start does not report running", FailedListenerStartDoesNotReportRunningAsync),
      ("listener fallback publishes and removes a per-instance endpoint", EndpointRegistrationLifecycleAsync),
      ("a new session removes endpoint records of processes that are gone", StaleEndpointRecordsAsync),
      ("parallel dispatch is serialized with exact waiting and active status", SerializedDispatchStatusAsync),
      ("native ExecutionResult bridge closes the lost continuation race", NativeExecutionResultBridgeAsync),
      ("completion diagnostics distinguish callback task and synchronization context", CallbackReturnDiagnosticsAsync),
      ("completion first-chance observer is bounded and data-free", CompletionFirstChanceObserverAsync),
      ("completion diagnostics retain a suppressed native continuation without fallback", CompletionDiagnosticsNoFallbackAsync),
      ("completion diagnostics record callback, commit, save, and GetResult outcomes", CompletionDiagnosticsOutcomesAsync),
      ("completion diagnostic sampling is bounded and stops safely", CompletionDiagnosticSamplingAsync),
      ("completion diagnostics are default-off without changing gate behavior", CompletionDiagnosticsDefaultOffAsync),
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

    var sharedTests = new HashSet<Func<Task>>
    {
      DirectGuardContractAsync, DrawingIdentityOrderingAsync, PrivateDrawingIdentityAsync,
      SaveAfterCommitOrderingAsync, SaveRequestValidationAsync, SaveFailureCompletesIdempotencyAsync,
      ReadOnlyDrawingSaveAsync, ScriptTimeLimitParameterAsync,
      RequireCleanAsync, BackupBeforeWriteAsync, WriteReceiptAsync, WriteJournalAsync, DescriptionEchoAsync,
      SerializedDispatchStatusAsync, RunningScriptProgressAndCancellationAsync, ProgressCleanupAsync,
      HealthBypassesSerializedQueueAsync, ApiLookupBypassesSerializedQueueAsync,
      CancellationAndErrorCleanupAsync, GateWaitMeasurementAsync, ExecuteResultSerializationAsync,
      IdempotencyFirstSuccessAndCompletedDuplicateAsync, IdempotencyInProgressDuplicateAsync,
      IdempotencyConflictAsync, IdempotencyFailureReleasesKeyAsync, IdempotencyCapacityEvictionAsync,
      IdempotencyInvalidKeyAsync,
    };

    try
    {
      foreach (var (name, run) in tests)
      {
        await run();
        Console.WriteLine($"PASS {name}");
      }

      foreach (var (name, run) in tests.Where(test => sharedTests.Contains(test.Run)))
      {
        using var host = new ModalTestHost();
        _modalHost = host;
        try
        {
          await run();
          Console.WriteLine($"PASS [modal] {name}");
        }
        finally { _modalHost = null; }
      }

      ResultSerializerTests.RunAll();
      ScriptCacheTests.RunAll();
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
      Environment.SetEnvironmentVariable(WriteJournal.DirectoryEnvironmentVariable, null);
      Environment.SetEnvironmentVariable(DrawingBackup.DirectoryEnvironmentVariable, null);
      if (Directory.Exists(SafetyDirectory))
      {
        Directory.Delete(SafetyDirectory, recursive: true);
      }
    }
  }

  private static Task ModalCommandRegistrationAsync()
  {
    var registration = typeof(PluginEntry).GetMethod(nameof(PluginEntry.RunModalCommand))!
      .GetCustomAttribute<Autodesk.AutoCAD.Runtime.CommandMethodAttribute>();
    Assert(registration?.GlobalName == ModalCommandAdmission.CommandName,
      "the production entry method must register the internal command name");
    var required = Autodesk.AutoCAD.Runtime.CommandFlags.NoHistory
      | Autodesk.AutoCAD.Runtime.CommandFlags.UsePickSet | Autodesk.AutoCAD.Runtime.CommandFlags.Redraw;
    var forbidden = Autodesk.AutoCAD.Runtime.CommandFlags.Transparent
      | Autodesk.AutoCAD.Runtime.CommandFlags.Session | Autodesk.AutoCAD.Runtime.CommandFlags.NoUndoMarker;
    Assert((registration!.Flags & required) == required,
      "the modal command must preserve implied selection and remain outside repeat history");
    Assert((registration.Flags & forbidden) == 0,
      "the shared writer must stay modal, document-scoped, and preserve normal Undo behavior");
    return Task.CompletedTask;
  }

  private static async Task ModalDispatchAsync()
  {
    ResetEnvironment();
    CivilExecution.UseNativeBackendOverrideForTests = false;
    ModalCommandAdmission.Initialize();
    var nativeCalls = Application.DocumentManager.CommandContextCallCount;
    using var stop = new CancellationTokenSource();
    var write = PluginRuntime.HandleRawRequestAsync(CreateGuardedWriteRequest("modal-write", "modal-body", saveDrawing: true), stop.Token);
    Application.RaiseIdle();
    ModalCommandAdmission.RunCommand();
    Assert(CurrentDatabase.TransactionManager.CommittedCount == 1 && CurrentDatabase.SaveAsCallCount == 1,
      "modal body must retain original commit and save behavior");
    Assert(CurrentDatabase.TransactionManager.ActiveCount == 0 && CurrentDocument.ActiveLockCount == 0,
      "body resources must be disposed before command end");
    Assert(!write.IsCompleted && PluginRuntime.GetStatus().OperationInProgress,
      "body exit cannot release the existing operation gate");
    stop.Cancel();
    CurrentDocument.EndCommand("UNRELATED");
    Assert(!write.IsCompleted, "neither cancellation nor unrelated command end can free started work");
    var read = SendAsync(CreateRequest("modal-read", readOnly: true));
    Assert(PluginRuntime.GetStatus().QueueDepth == 1, "next operation must wait behind the same gate");
    CurrentDocument.EndCommand(ModalCommandAdmission.CommandName);
    AssertResult(await write.WaitAsync(TimeSpan.FromSeconds(2)), "ok");
    // The gate continuation queues the next request asynchronously.
    for (var i = 0; i < 100 && !read.IsCompleted; i++)
    {
      Application.RaiseIdle();
      ModalCommandAdmission.RunCommand();
      CurrentDocument.EndCommand(ModalCommandAdmission.CommandName);
      await Task.Delay(1);
    }
    AssertResult(await read.WaitAsync(TimeSpan.FromSeconds(2)), "ok");
    Application.RaiseIdle();
    Assert(Application.DocumentManager.CommandContextCallCount == nativeCalls,
      "modal dispatch must never call ExecuteInCommandContextAsync");
    Assert(CurrentDocument.CommandHandlerCount == 0 && RoslynExecutor.CallCount == 2,
      "both operations run once and detach their handlers");
    ModalCommandAdmission.Terminate();
    AssertCleanStatus();
  }

  private static async Task ModalAdmissionSafetyAsync()
  {
    ResetEnvironment();
    ModalCommandAdmission.Initialize();
    var calls = 0;
    using var cancel = new CancellationTokenSource();
    var stale = ModalCommandAdmission.ExecuteAsync(() => calls++, cancel.Token, () => false);
    Application.RaiseIdle();
    var staleToken = CurrentDocument.Editor.CommandToken;
    cancel.Cancel();
    try { await stale; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
    var next = ModalCommandAdmission.ExecuteAsync(() => calls++, CancellationToken.None, () => false);
    Application.RaiseIdle();
    var token = CurrentDocument.Editor.CommandToken;
    CurrentDocument.Editor.CommandToken = staleToken;
    ModalCommandAdmission.RunCommand();
    CurrentDocument.EndCommand(ModalCommandAdmission.CommandName);
    Assert(calls == 0 && !next.IsCompleted, "late abandoned token must not consume or finish a newer request");
    CurrentDocument.Editor.CommandToken = token;
    ModalCommandAdmission.RunCommand();
    CurrentDocument.EndCommand(ModalCommandAdmission.CommandName);
    await next;
    Assert(calls == 1, "current token executes once");

    var original = CurrentDocument;
    var wrong = ModalCommandAdmission.ExecuteAsync(() => calls++, CancellationToken.None, () => false);
    Application.RaiseIdle();
    var other = new Document(new Database());
    other.Editor.CommandToken = original.Editor.CommandToken;
    Application.DocumentManager.MdiActiveDocument = other;
    ModalCommandAdmission.RunCommand();
    try { await wrong; throw new Exception("Expected wrong-document rejection"); }
    catch (JsonRpcDispatchException ex) { Assert(ex.Code == "CIVIL3D.DRAWING_MISMATCH", "wrong document error"); }
    Application.DocumentManager.MdiActiveDocument = original;
    Application.RaiseIdle();
    Assert(calls == 1, "wrong document must never execute");

    ModalCommandAdmission.StartDeadlineOverrideForTests = TimeSpan.FromMilliseconds(10);
    var timedOut = ModalCommandAdmission.ExecuteAsync(() => calls++, CancellationToken.None, () => false);
    try { await timedOut; throw new Exception("Expected admission timeout"); }
    catch (JsonRpcDispatchException ex) { Assert(ex.Code == "CIVIL3D.COMMAND_CONTEXT_TIMEOUT", "start deadline error"); }
    Application.RaiseIdle();
    Assert(calls == 1 && original.CommandHandlerCount == 0, "timeout must leave no executable work or handlers");
    ModalCommandAdmission.StartDeadlineOverrideForTests = null;

    var bodyError = ModalCommandAdmission.ExecuteAsync(() => throw new InvalidOperationException("body"), CancellationToken.None, () => false);
    Application.RaiseIdle();
    ModalCommandAdmission.RunCommand();
    Assert(!bodyError.IsCompleted, "a body error still needs the real host end");
    original.EndCommand(ModalCommandAdmission.CommandName);
    try { await bodyError; throw new Exception("Expected body failure"); } catch (InvalidOperationException) { }
    Application.RaiseIdle();
    var committedCancel = ModalCommandAdmission.ExecuteAsync(() => { }, CancellationToken.None, () => true);
    Application.RaiseIdle();
    ModalCommandAdmission.RunCommand();
    original.CancelCommand(ModalCommandAdmission.CommandName);
    try { await committedCancel; throw new Exception("Expected host cancellation"); }
    catch (JsonRpcDispatchException ex) { Assert(ex.OperationCommitted, "host cancellation must preserve a completed commit"); }
    Application.RaiseIdle();
    Assert(original.CommandHandlerCount == 0, "error paths must detach once");
    ModalCommandAdmission.Terminate();
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
    using var diagnostics = SetCompletionDiagnostics("1");
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
    var registrationRace = PluginRuntime.GetStatus().LastCompletionDiagnostics
      ?? throw new InvalidOperationException("registration-race diagnostics must be retained");
    Assert(registrationRace.PostRegistrationNativeIsCompleted == true,
      "registration-race diagnostics must preserve the native post-registration completion read");
    Assert(registrationRace.ContinuationInvocations == 0,
      "a managed fallback must not be recorded as a native continuation invocation");
    AssertCleanStatus();
  }

  private static async Task CallbackReturnDiagnosticsAsync()
  {
    using var diagnostics = SetCompletionDiagnostics("1");
    ResetEnvironment();
    var originalContext = SynchronizationContext.Current;
    try
    {
      SynchronizationContext.SetSynchronizationContext(null);
      var nullContextProgress = new OperationProgress();
      await CommandContextAdmission.ExecuteAsync(
        Application.DocumentManager,
        _ => Task.CompletedTask,
        null,
        CancellationToken.None,
        nullContextProgress
      );
      var nullContextStatus = nullContextProgress.GetStatus();
      var nullContext = nullContextStatus.CompletionDiagnostics
        ?? throw new InvalidOperationException("enabled callback diagnostics must be retained");
      Assert(nullContext.CallbackSynchronizationContextBefore == "Null" &&
        nullContext.CallbackSynchronizationContextAfter == "Null" &&
        nullContext.ReturnedCallbackTaskStatus == "RanToCompletion" &&
        nullContext.CallbackReturnTaskScheduler == "DefaultThreadPool" &&
        nullContext.ReturnedCallbackTaskObservedElapsedMs is double callbackTaskObservedElapsedMs &&
        callbackTaskObservedElapsedMs >= 0 &&
        callbackTaskObservedElapsedMs <= nullContextStatus.OperationElapsedMs,
        "a synchronous callback must record only fixed null-context and completed-task scalars");

      SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
      var otherContextProgress = new OperationProgress();
      await CommandContextAdmission.ExecuteAsync(
        Application.DocumentManager,
        _ => Task.CompletedTask,
        null,
        CancellationToken.None,
        otherContextProgress
      );
      var otherContext = otherContextProgress.GetStatus().CompletionDiagnostics
        ?? throw new InvalidOperationException("enabled callback diagnostics must be retained");
      Assert(otherContext.CallbackSynchronizationContextBefore == "Other" &&
        otherContext.CallbackSynchronizationContextAfter == "Other",
        "a non-Autodesk synchronization context must remain a fixed Other classification");

      SynchronizationContext.SetSynchronizationContext(
        new TestAutodeskNamespaceSynchronizationContext()
      );
      var namespaceContextProgress = new OperationProgress();
      await CommandContextAdmission.ExecuteAsync(
        Application.DocumentManager,
        _ => Task.CompletedTask,
        null,
        CancellationToken.None,
        namespaceContextProgress
      );
      var namespaceContext = namespaceContextProgress.GetStatus().CompletionDiagnostics
        ?? throw new InvalidOperationException("enabled callback diagnostics must be retained");
      Assert(namespaceContext.CallbackSynchronizationContextBefore == "Other" &&
        namespaceContext.CallbackSynchronizationContextAfter == "Other",
        "an unrelated Autodesk namespace context must remain Other");

      SynchronizationContext.SetSynchronizationContext(
        new Autodesk.AutoCAD.Runtime.SynchronizationContext()
      );
      var autodeskContextProgress = new OperationProgress();
      await CommandContextAdmission.ExecuteAsync(
        Application.DocumentManager,
        _ => Task.CompletedTask,
        null,
        CancellationToken.None,
        autodeskContextProgress
      );
      var autodeskContext = autodeskContextProgress.GetStatus().CompletionDiagnostics
        ?? throw new InvalidOperationException("enabled callback diagnostics must be retained");
      Assert(autodeskContext.CallbackSynchronizationContextBefore == "AutodeskAutoCAD" &&
        autodeskContext.CallbackSynchronizationContextAfter == "AutodeskAutoCAD",
        "an Autodesk synchronization context must not expose its implementation type");

      SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
      var changedContextProgress = new OperationProgress();
      await CommandContextAdmission.ExecuteAsync(
        Application.DocumentManager,
        _ =>
        {
          SynchronizationContext.SetSynchronizationContext(
            new Autodesk.AutoCAD.Runtime.SynchronizationContext()
          );
          return Task.CompletedTask;
        },
        null,
        CancellationToken.None,
        changedContextProgress
      );
      var changedContext = changedContextProgress.GetStatus().CompletionDiagnostics
        ?? throw new InvalidOperationException("enabled callback diagnostics must be retained");
      Assert(changedContext.CallbackSynchronizationContextBefore == "Other" &&
        changedContext.CallbackSynchronizationContextAfter == "AutodeskAutoCAD",
        "callback context diagnostics must capture the immediate post-return context");

      SynchronizationContext.SetSynchronizationContext(null);
      var pendingCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var pendingProgress = new OperationProgress();
      var pendingOperation = CommandContextAdmission.ExecuteAsync(
        Application.DocumentManager,
        _ => pendingCallback.Task,
        null,
        CancellationToken.None,
        pendingProgress
      );
      await WaitUntilAsync(() => ReferenceEquals(
        Application.DocumentManager.LastCallbackTask,
        pendingCallback.Task
      ));
      var pendingSnapshot = pendingProgress.GetStatus().CompletionDiagnostics
        ?? throw new InvalidOperationException("enabled callback diagnostics must be retained");
      Assert(!pendingOperation.IsCompleted &&
        pendingSnapshot.InitialNativeIsCompleted == false &&
        pendingSnapshot.PostRegistrationNativeIsCompleted == false &&
        pendingSnapshot.ReturnedCallbackTaskStatus == "WaitingForActivation",
        "the original pending callback task must remain native-pending without a wrapper");
      pendingCallback.SetResult();
      await pendingOperation;

      var nullTaskProgress = new OperationProgress();
      var nullTaskOperation = CommandContextAdmission.ExecuteAsync(
        Application.DocumentManager,
        _ => null!,
        null,
        CancellationToken.None,
        nullTaskProgress
      );
      var nullTaskThrew = false;
      try
      {
        await nullTaskOperation;
      }
      catch (NullReferenceException)
      {
        nullTaskThrew = true;
      }
      var nullTaskSnapshot = nullTaskProgress.GetStatus().CompletionDiagnostics
        ?? throw new InvalidOperationException("enabled callback diagnostics must be retained");
      Assert(nullTaskThrew && Application.DocumentManager.LastCallbackTask == null &&
        nullTaskSnapshot.ReturnedCallbackTaskStatus == "Null",
        "diagnostics must return a callback null unchanged instead of masking it");

      var expectedCallbackException = new InvalidOperationException("expected callback failure");
      Exception? observedCallbackException = null;
      try
      {
        await CommandContextAdmission.ExecuteAsync(
          Application.DocumentManager,
          _ => throw expectedCallbackException,
          null,
          CancellationToken.None,
          new OperationProgress()
        );
      }
      catch (Exception exception)
      {
        observedCallbackException = exception;
      }
      Assert(ReferenceEquals(observedCallbackException, expectedCallbackException),
        "enabled diagnostics must preserve callback exception identity");

      var disabledProgress = new OperationProgress(false);
      await CommandContextAdmission.ExecuteAsync(
        Application.DocumentManager,
        _ => Task.CompletedTask,
        null,
        CancellationToken.None,
        disabledProgress
      );
      Assert(disabledProgress.GetStatus().CompletionDiagnostics == null,
        "the default-off path must not create callback task or context diagnostics");
    }
    finally
    {
      SynchronizationContext.SetSynchronizationContext(originalContext);
    }
  }

  private static async Task CompletionFirstChanceObserverAsync()
  {
    using var diagnostics = SetCompletionDiagnostics("1");
    ResetEnvironment();
    Assert(FirstChanceCompletionObserver.ActiveSubscriptionsForTests == 0,
      "first-chance tests must begin without a process-global subscription");

    var progress = new OperationProgress();
    using var observer = FirstChanceCompletionObserver.Start(progress)
      ?? throw new InvalidOperationException("enabled diagnostics must subscribe");
    Assert(FirstChanceCompletionObserver.ActiveSubscriptionsForTests == 1,
      "enabled diagnostics must install exactly one short-lived observer");
    try
    {
      throw new InvalidOperationException("unrelated-first-chance-marker");
    }
    catch (InvalidOperationException)
    {
      // Deliberately unrelated to the exact Autodesk continuation/Post frames.
    }
    Assert(progress.GetStatus().CompletionDiagnostics?.MatchingFirstChanceExceptionCount == 0 &&
      progress.GetStatus().CompletionDiagnostics?.FirstChanceObserverState == "Running" &&
      FirstChanceCompletionObserver.ActiveSubscriptionsForTests == 1,
      "unrelated first-chance exceptions must leave the bounded observer active");
    try
    {
      new Autodesk.AutoCAD.Runtime.SynchronizationContext().Post(null!, null);
    }
    catch (InvalidOperationException)
    {
      // The observer only sees this first-chance exception; normal propagation is unchanged.
    }
    var detected = progress.GetStatus().CompletionDiagnostics
      ?? throw new InvalidOperationException("enabled diagnostics must retain a snapshot");
    Assert(detected.MatchingFirstChanceExceptionCount == 1,
      "the exact Autodesk continuation/Post path must record one first-chance event");
    Assert(detected.FirstMatchingFirstChanceExceptionCategory == "InvalidOperation",
      "the observer must retain the fixed exception category only");
    Assert(detected.FirstMatchingSynchronizationContext == "Null" &&
      detected.FirstMatchingTaskScheduler == "DefaultThreadPool",
      "the observer must retain fixed event-time context and scheduler categories only");
    Assert(detected.FirstMatchingStackHasExecutionResultContinuation == true &&
      detected.FirstMatchingStackHasSynchronizationContextPost == true,
      "the observer must classify both exact Autodesk stack frames");
    Assert(detected.FirstMatchingFirstChanceExceptionElapsedMs >= 0,
      "the observer must record an operation-relative timestamp");
    Assert(FirstChanceCompletionObserver.ActiveSubscriptionsForTests == 0,
      "the first matching event must immediately unsubscribe the observer");
    Assert(detected.FirstChanceObserverState == "Matched",
      "the first matching event must publish the fixed matched observer state");
    Assert(!JsonSerializer.Serialize(detected).Contains("firstchance-private-marker", StringComparison.Ordinal),
      "first-chance diagnostics must not retain exception messages or stack text");
    observer.Dispose();
    Assert(FirstChanceCompletionObserver.ActiveSubscriptionsForTests == 0,
      "disposing the observer must unsubscribe it immediately");

    FirstChanceCompletionObserver.LifetimeOverrideForTests = TimeSpan.FromMilliseconds(10);
    try
    {
      var expiringProgress = new OperationProgress();
      var expiring = FirstChanceCompletionObserver.Start(expiringProgress)
        ?? throw new InvalidOperationException("enabled diagnostics must subscribe");
      await WaitUntilAsync(() => FirstChanceCompletionObserver.ActiveSubscriptionsForTests == 0);
      Assert(expiringProgress.GetStatus().CompletionDiagnostics?.FirstChanceObserverState == "Expired",
        "the hard deadline must publish the fixed expired observer state");
      expiring.Dispose();
    }
    finally
    {
      FirstChanceCompletionObserver.LifetimeOverrideForTests = null;
    }

    FirstChanceCompletionObserver.LifetimeOverrideForTests = TimeSpan.FromMilliseconds(-2);
    try
    {
      var failedProgress = new OperationProgress();
      Assert(FirstChanceCompletionObserver.Start(failedProgress) == null &&
        FirstChanceCompletionObserver.ActiveSubscriptionsForTests == 0 &&
        failedProgress.GetStatus().CompletionDiagnostics?.FirstChanceObserverState == "Failed",
        "observer setup failure must clean up and leave native scheduling available");
    }
    finally
    {
      FirstChanceCompletionObserver.LifetimeOverrideForTests = null;
    }

    var disabled = new OperationProgress(false);
    Assert(FirstChanceCompletionObserver.Start(disabled) == null &&
      FirstChanceCompletionObserver.ActiveSubscriptionsForTests == 0,
      "default-off diagnostics must not subscribe to process-global first-chance events");

    var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var integrationProgress = new OperationProgress();
    var operation = CommandContextAdmission.ExecuteAsync(
      Application.DocumentManager,
      _ => pending.Task,
      null,
      CancellationToken.None,
      integrationProgress
    );
    await WaitUntilAsync(() => ReferenceEquals(Application.DocumentManager.LastCallbackTask, pending.Task));
    Assert(ReferenceEquals(Application.DocumentManager.LastCallbackTask, pending.Task),
      "the observer must not wrap or replace the callback task");
    pending.SetResult();
    await operation;
    Assert(FirstChanceCompletionObserver.ActiveSubscriptionsForTests == 0,
      "native scheduling completion must unsubscribe the observer");
    Assert(integrationProgress.GetStatus().CompletionDiagnostics?.FirstChanceObserverState == "Stopped",
      "native scheduling completion must publish the fixed stopped observer state");
  }

  private static async Task CompletionDiagnosticsNoFallbackAsync()
  {
    using var diagnostics = SetCompletionDiagnostics("1");
    ResetEnvironment();
    CommandContextAdmission.NativeSampleOffsetsOverrideForTests =
      [TimeSpan.Zero, TimeSpan.FromMilliseconds(250)];
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    Application.DocumentManager.SuppressCompletionContinuation = true;
    var callbackExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseNativeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<string>? first = null;
    Task<string>? second = null;
    Application.DocumentManager.AfterCommandContextAsync = async () =>
    {
      callbackExited.TrySetResult();
      await releaseNativeCompletion.Task;
    };

    try
    {
      first = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
        "completion-suppressed-first",
        "completion-suppressed-first"
      )));
      await callbackExited.Task;
      await WaitUntilAsync(() =>
        PluginRuntime.GetStatus().CompletionDiagnostics?.PostRegistrationNativeIsCompleted == false);

      var beforeNativeCompletion = await GetHealthAsync();
      var beforeDiagnostic = beforeNativeCompletion["completionDiagnostics"]?.AsObject();
      var beforeStatusDiagnostic = PluginRuntime.GetStatus().CompletionDiagnostics
        ?? throw new InvalidOperationException("active completion diagnostics must be retained");
      Assert(beforeDiagnostic != null,
        "active health must expose a data-free completion diagnostic snapshot");
      Assert(beforeStatusDiagnostic.InitialNativeIsCompleted == false,
        "the native result must initially be incomplete before the held outer task finishes");
      Assert(beforeStatusDiagnostic.PostRegistrationNativeIsCompleted == false,
        "the post-registration recheck must remain false before native completion");
      Assert(beforeStatusDiagnostic.ContinuationInvocations == 0,
        "no native continuation may be invented before completion");
      Assert(beforeStatusDiagnostic.GetResultOutcome == "NotStarted",
        "GetResult must wait for native notification");
      Assert(beforeStatusDiagnostic.CallbackEnteredElapsedMs != null &&
        beforeStatusDiagnostic.CallbackExitedElapsedMs is double callbackExitedElapsedMs &&
        beforeStatusDiagnostic.ReturnedCallbackTaskObservedElapsedMs is double callbackTaskObservedElapsedMs &&
        callbackTaskObservedElapsedMs >= callbackExitedElapsedMs,
        "callback entry, exit, and returned-task observation must use operation-relative timing");
      // The remembered active drawing is a deliberate health field of its own;
      // everything else, the diagnostics above all, stays free of drawing data.
      var withoutActiveDrawing = beforeNativeCompletion.DeepClone().AsObject();
      withoutActiveDrawing.Remove("activeDrawing");
      var healthJson = withoutActiveDrawing.ToJsonString();
      Assert(!healthJson.Contains("Target.dwg", StringComparison.Ordinal) &&
        !healthJson.Contains(ActualFingerprint.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
        !healthJson.Contains("completion-suppressed-first", StringComparison.Ordinal),
        "health diagnostics must not expose drawing identity or caller code");

      second = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
        "completion-suppressed-second",
        "completion-suppressed-second"
      )));
      await WaitUntilAsync(() => PluginRuntime.GetStatus().QueueDepth == 1);

      releaseNativeCompletion.SetResult();
      await WaitUntilAsync(() =>
        PluginRuntime.GetStatus().CompletionDiagnostics?.LastSampledNativeIsCompleted == true);
      var heldHealth = await GetHealthAsync();
      var heldDiagnostic = PluginRuntime.GetStatus().CompletionDiagnostics;
      Assert(!first.IsCompleted,
        "observing a native completed state without its continuation must not finish the operation");
      Assert(PluginRuntime.GetStatus().OperationInProgress && PluginRuntime.GetStatus().QueueDepth == 1,
        "a missing native continuation must retain the serialized gate and its waiter");
      Assert(heldDiagnostic?.ContinuationInvocations == 0,
        "sampled completion must not fabricate a native continuation");
      Assert(heldDiagnostic?.GetResultOutcome == "NotStarted",
        "sampled completion must not call GetResult as a fallback");
      Assert(heldDiagnostic?.LastSampledNativeIsCompleted == true,
        "the bounded observer must record the native completed state as data only");

      Application.DocumentManager.SuppressCompletionContinuation = false;
      Assert(Application.DocumentManager.DeliverSuppressedCompletionContinuation(),
        "the test stub must explicitly release the suppressed native continuation for cleanup");
      AssertResult(await first, "completion-suppressed-first");
      AssertResult(await second, "completion-suppressed-second");
    }
    finally
    {
      Application.DocumentManager.SuppressCompletionContinuation = false;
      releaseNativeCompletion.TrySetResult();
      Application.DocumentManager.DeliverSuppressedCompletionContinuation();
      if (first != null && !first.IsCompleted) await first.WaitAsync(TimeSpan.FromSeconds(1));
      if (second != null && !second.IsCompleted) await second.WaitAsync(TimeSpan.FromSeconds(1));
      Application.DocumentManager.AfterCommandContextAsync = null;
      CommandContextAdmission.NativeSampleOffsetsOverrideForTests = null;
    }

    AssertCleanStatus();
  }

  private static async Task CompletionDiagnosticsOutcomesAsync()
  {
    using var diagnostics = SetCompletionDiagnostics("1");
    ResetEnvironment();
    CommandContextAdmission.NativeSampleOffsetsOverrideForTests = [TimeSpan.FromSeconds(1)];
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    var callbackExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseNativeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Application.DocumentManager.AfterCommandContextAsync = async () =>
    {
      callbackExited.TrySetResult();
      await releaseNativeCompletion.Task;
    };
    var successfulSave = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
      "completion-outcome-success",
      "completion-outcome-success",
      saveDrawing: true
    )));
    await callbackExited.Task;
    await WaitUntilAsync(() =>
      PluginRuntime.GetStatus().CompletionDiagnostics is
      {
        ContinuationRegisteredElapsedMs: not null,
        PostRegistrationNativeIsCompleted: false,
      });
    var active = PluginRuntime.GetStatus().CompletionDiagnostics
      ?? throw new InvalidOperationException("active completion diagnostics must be retained");
    Assert(active.CapturedException == false,
      "a completed callback with successful commit and save must not report a captured exception");
    Assert(active.CommitOutcome == "Succeeded" && active.SaveOutcome == "Succeeded",
      "diagnostics must distinguish a successful commit and save before native completion");
    Assert(active.GetResultOutcome == "NotStarted" && active.ContinuationInvocations == 0,
      "GetResult must remain pending until the actual native continuation arrives");
    releaseNativeCompletion.SetResult();
    AssertResult(await successfulSave, "completion-outcome-success");
    var successfulSnapshot = PluginRuntime.GetStatus().LastCompletionDiagnostics
      ?? throw new InvalidOperationException("successful completion diagnostics must be retained");
    Assert(successfulSnapshot.ContinuationInvocations == 1 &&
      successfulSnapshot.GetResultOutcome == "Succeeded",
      "a delivered native continuation must lead to successful GetResult exactly once");
    Assert(successfulSnapshot.SamplingState == "Stopped",
      "the sparse observer must stop when the native continuation completes the bridge");

    ResetEnvironment();
    RoslynExecutor.Handler = (_, _) => throw new InvalidOperationException("expected action failure");
    AssertError(
      await SendAsync(CreateGuardedWriteRequest("completion-outcome-action", "action-failure")),
      "CIVIL3D.TRANSACTION_FAILED"
    );
    var actionFailure = PluginRuntime.GetStatus().LastCompletionDiagnostics;
    Assert(actionFailure?.CapturedException == true &&
      actionFailure.CommitOutcome == "NotStarted" && actionFailure.SaveOutcome == "NotRequested",
      "a captured action exception must be separated from commit and save outcomes");

    ResetEnvironment();
    CurrentDatabase.TransactionManager.CommitException = new InvalidOperationException("expected commit failure");
    AssertError(
      await SendAsync(CreateGuardedWriteRequest("completion-outcome-commit", "commit-failure")),
      "CIVIL3D.TRANSACTION_FAILED"
    );
    var commitFailure = PluginRuntime.GetStatus().LastCompletionDiagnostics;
    Assert(commitFailure?.CapturedException == true &&
      commitFailure.CommitOutcome == "Threw" && commitFailure.SaveOutcome == "NotRequested",
      "a commit failure must be recorded separately from a captured script exception");

    ResetEnvironment();
    CurrentDatabase.SaveAsException = new InvalidOperationException("expected save failure");
    AssertError(
      await SendAsync(CreateGuardedWriteRequest(
        "completion-outcome-save",
        "save-failure",
        saveDrawing: true
      )),
      "CIVIL3D.SAVE_FAILED"
    );
    var saveFailure = PluginRuntime.GetStatus().LastCompletionDiagnostics;
    Assert(saveFailure?.CapturedException == true &&
      saveFailure.CommitOutcome == "Succeeded" && saveFailure.SaveOutcome == "Threw",
      "a save failure must retain the completed in-memory commit outcome");

    ResetEnvironment();
    CommandContextAdmission.NativeSampleOffsetsOverrideForTests = [TimeSpan.FromSeconds(1)];
    callbackExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    releaseNativeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Application.DocumentManager.AfterCommandContextAsync = async () =>
    {
      callbackExited.TrySetResult();
      await releaseNativeCompletion.Task;
    };
    Application.DocumentManager.CommandContextCompletionException =
      new InvalidOperationException("expected native completion fault");
    var nativeFault = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
      "completion-outcome-native-fault",
      "native-fault"
    )));
    await callbackExited.Task;
    await WaitUntilAsync(() =>
      PluginRuntime.GetStatus().CompletionDiagnostics is
      {
        ContinuationRegisteredElapsedMs: not null,
        PostRegistrationNativeIsCompleted: false,
      });
    releaseNativeCompletion.SetResult();
    AssertError(await nativeFault, "CIVIL3D.TRANSACTION_FAILED");
    var nativeFaultSnapshot = PluginRuntime.GetStatus().LastCompletionDiagnostics;
    Assert(nativeFaultSnapshot?.CapturedException == false &&
      nativeFaultSnapshot.ContinuationInvocations == 1 &&
      nativeFaultSnapshot.GetResultOutcome == "Threw",
      "a native completion fault must be observed through continuation and GetResult, not as callback failure");
    Application.DocumentManager.AfterCommandContextAsync = null;
    CommandContextAdmission.NativeSampleOffsetsOverrideForTests = null;
    FirstChanceCompletionObserver.LifetimeOverrideForTests = null;
    AssertCleanStatus();
  }

  private static async Task CompletionDiagnosticSamplingAsync()
  {
    using var diagnostics = SetCompletionDiagnostics("1");
    ResetEnvironment();
    CommandContextAdmission.NativeSampleOffsetsOverrideForTests =
      [TimeSpan.Zero, TimeSpan.Zero];
    var exhaustedProgress = new OperationProgress();
    await CommandContextAdmission.SampleNativeCompletionAsync(
      () => false,
      exhaustedProgress,
      CancellationToken.None
    );
    var exhausted = exhaustedProgress.GetStatus().CompletionDiagnostics
      ?? throw new InvalidOperationException("opt-in sampler must retain diagnostics");
    Assert(exhausted.SampleCount == 2 && exhausted.LastSampledNativeIsCompleted == false &&
      exhausted.SamplingState == "Exhausted",
      "a false native state must use the configured sparse sample budget and then stop");

    var abandonedProgress = new OperationProgress();
    using var abandoned = new CancellationTokenSource();
    abandoned.Cancel();
    await CommandContextAdmission.SampleNativeCompletionAsync(
      () => throw new InvalidOperationException("abandoned sampler must not read native state"),
      abandonedProgress,
      abandoned.Token
    );
    var abandonedSnapshot = abandonedProgress.GetStatus().CompletionDiagnostics
      ?? throw new InvalidOperationException("opt-in sampler must retain diagnostics");
    Assert(abandonedSnapshot.SampleCount == 0 && abandonedSnapshot.SamplingState == "Stopped",
      "an abandoned observer start must stop without reading native state or surfacing an exception");

    var readFaultProgress = new OperationProgress();
    await CommandContextAdmission.SampleNativeCompletionAsync(
      () => throw new InvalidOperationException("private sampler read fault"),
      readFaultProgress,
      CancellationToken.None
    );
    var readFault = readFaultProgress.GetStatus().CompletionDiagnostics
      ?? throw new InvalidOperationException("opt-in sampler must retain diagnostics");
    Assert(readFault.SampleCount == 0 && readFault.SamplingState == "ReadFailed" &&
      !JsonSerializer.Serialize(readFault).Contains("private sampler read fault", StringComparison.Ordinal),
      "a sampler read fault must be data-free and must not affect execution");
    CommandContextAdmission.NativeSampleOffsetsOverrideForTests = null;
  }

  private static async Task CompletionDiagnosticsDefaultOffAsync()
  {
    using var diagnostics = SetCompletionDiagnostics(null);
    ResetEnvironment();
    CommandContextAdmission.NativeSampleOffsetsOverrideForTests = [TimeSpan.Zero];
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    AssertResult(
      await SendAsync(CreateGuardedWriteRequest("completion-disabled-complete", "completion-disabled-complete")),
      "completion-disabled-complete"
    );
    var completedStatus = PluginRuntime.GetStatus();
    Assert(completedStatus.CompletionDiagnostics == null &&
      completedStatus.LastCompletionDiagnostics == null,
      "a default-off operation must create and retain no completion diagnostic snapshot");

    ResetEnvironment();
    CommandContextAdmission.NativeSampleOffsetsOverrideForTests = [TimeSpan.Zero];
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    Application.DocumentManager.SuppressCompletionContinuation = true;
    var callbackExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseNativeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<string>? first = null;
    Task<string>? second = null;
    Application.DocumentManager.AfterCommandContextAsync = async () =>
    {
      callbackExited.TrySetResult();
      await releaseNativeCompletion.Task;
    };

    try
    {
      first = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
        "completion-disabled-held",
        "completion-disabled-held"
      )));
      await callbackExited.Task;
      second = Task.Run(() => SendAsync(CreateGuardedWriteRequest(
        "completion-disabled-waiter",
        "completion-disabled-waiter"
      )));
      await WaitUntilAsync(() => PluginRuntime.GetStatus().QueueDepth == 1);
      releaseNativeCompletion.SetResult();
      await Task.Delay(25);

      var heldStatus = PluginRuntime.GetStatus();
      Assert(!first.IsCompleted && heldStatus.OperationInProgress && heldStatus.QueueDepth == 1,
        "default-off diagnostics must not release a native operation that is still missing its continuation");
      Assert(heldStatus.CompletionDiagnostics == null && heldStatus.LastCompletionDiagnostics == null,
        "a default-off held operation must not allocate snapshots or start an observer");

      Application.DocumentManager.SuppressCompletionContinuation = false;
      Assert(Application.DocumentManager.DeliverSuppressedCompletionContinuation(),
        "the suppressed native continuation must remain explicitly deliverable for cleanup");
      AssertResult(await first, "completion-disabled-held");
      AssertResult(await second, "completion-disabled-waiter");
    }
    finally
    {
      Application.DocumentManager.SuppressCompletionContinuation = false;
      releaseNativeCompletion.TrySetResult();
      Application.DocumentManager.DeliverSuppressedCompletionContinuation();
      if (first != null && !first.IsCompleted) await first.WaitAsync(TimeSpan.FromSeconds(1));
      if (second != null && !second.IsCompleted) await second.WaitAsync(TimeSpan.FromSeconds(1));
      Application.DocumentManager.AfterCommandContextAsync = null;
      CommandContextAdmission.NativeSampleOffsetsOverrideForTests = null;
    }

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

  private static async Task ReadOnlyDrawingSaveAsync()
  {
    // Without this check the write would commit in memory and only the save
    // at the very end would fail.
    ResetEnvironment();
    CurrentDocument.IsReadOnly = true;
    var refused = await SendAsync(CreateGuardedWriteRequest("save-into-read-only", "must-not-run", saveDrawing: true));
    AssertError(refused, "CIVIL3D.DRAWING_READ_ONLY");
    AssertNoCivilResourcesOpened();
    Assert(CurrentDatabase.SaveAsCallCount == 0, "a read-only drawing must not be saved");
    Assert(CurrentDatabase.TransactionManager.CommittedCount == 0, "nothing may be committed before the refusal");

    // The same key can be used again after the drawing is reopened for writing.
    ResetEnvironment();
    CurrentDocument.IsReadOnly = true;
    AssertError(
      await SendAsync(CreateIdempotentWriteRequest("read-only-first", "write:read-only", "value", saveDrawing: true)),
      "CIVIL3D.DRAWING_READ_ONLY"
    );
    CurrentDocument.IsReadOnly = false;
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    AssertResult(
      await SendAsync(CreateIdempotentWriteRequest("read-only-second", "write:read-only", "value", saveDrawing: true)),
      "value"
    );
    Assert(CurrentDatabase.SaveAsCallCount == 1, "the writable drawing must be saved once");

    // In-memory edits of a read-only drawing stay possible when no save is asked for.
    ResetEnvironment();
    CurrentDocument.IsReadOnly = true;
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    AssertResult(await SendAsync(CreateGuardedWriteRequest("memory-only", "memory-only")), "memory-only");
    Assert(CurrentDatabase.TransactionManager.CommittedCount == 1, "a write without save must still commit");
    Assert(CurrentDatabase.SaveAsCallCount == 0, "no save was requested");
  }

  private static string CreateSafetyWriteRequest(
    string id,
    string code,
    string path,
    Action<JsonObject> configure)
  {
    var request = JsonNode.Parse(CreateRequest(
      id,
      readOnly: false,
      includeExpectedDrawing: true,
      expectedDrawing: Guard(path, ActualFingerprint),
      code: code
    ))!.AsObject();
    configure(request["params"]!.AsObject());
    return request.ToJsonString();
  }

  private static string[] ReadJournalLines()
    => Directory.Exists(JournalDirectory)
      ? Directory.GetFiles(JournalDirectory, "*.jsonl").SelectMany(File.ReadAllLines).ToArray()
      : Array.Empty<string>();

  private static void ClearSafetyFiles()
  {
    if (Directory.Exists(SafetyDirectory)) Directory.Delete(SafetyDirectory, recursive: true);
  }

  private static async Task RequireCleanAsync()
  {
    ResetEnvironment();
    Application.DbMod = 5;
    var refused = await SendAsync(CreateSafetyWriteRequest(
      "clean-refused", "must-not-run", ActualPath, p => p["requireClean"] = true));
    AssertError(refused, "CIVIL3D.DRAWING_NOT_CLEAN");
    Assert(JsonNode.Parse(refused)!["error"]!["message"]!.GetValue<string>()
      .Contains("without requireClean", StringComparison.Ordinal),
      "the refusal must name the switch the caller set");
    AssertNoCivilResourcesOpened();
    var refusedReceipt = JsonNode.Parse(refused)!["receipt"]?.AsObject();
    Assert(refusedReceipt?["dbmodBefore"]?.GetValue<int>() == 5 && refusedReceipt["committed"]?.GetValue<bool>() == false,
      "a refusal must still say what state the drawing was found in");

    // The same drawing is written when the caller does not ask for the check.
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    AssertResult(await SendAsync(CreateGuardedWriteRequest("clean-not-asked", "written")), "written");

    ResetEnvironment();
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    AssertResult(
      await SendAsync(CreateSafetyWriteRequest("clean-ok", "clean-ok", ActualPath, p => p["requireClean"] = true)),
      "clean-ok");
    Assert(CurrentDatabase.TransactionManager.CommittedCount == 1, "a clean drawing must be written");

    // The switches belong to writes only, and are validated before anything runs.
    ResetEnvironment();
    foreach (var name in new[] { "requireClean", "backup" })
    {
      var query = JsonNode.Parse(CreateRequest("clean-query", readOnly: true, code: "must-not-run"))!.AsObject();
      query["params"]!.AsObject()[name] = true;
      AssertError(await SendAsync(query.ToJsonString()), "CIVIL3D.INVALID_INPUT");
    }
    AssertError(
      await SendAsync(CreateSafetyWriteRequest("clean-text", "must-not-run", ActualPath, p => p["requireClean"] = "yes")),
      "CIVIL3D.INVALID_INPUT");
    AssertNoCivilResourcesOpened();
  }

  private static async Task BackupBeforeWriteAsync()
  {
    ClearSafetyFiles();
    var drawingDirectory = Path.Combine(SafetyDirectory, "drawings");
    Directory.CreateDirectory(drawingDirectory);
    var drawingPath = Path.Combine(drawingDirectory, "Út terv.dwg");
    File.WriteAllText(drawingPath, "saved drawing content");

    // The copy exists before the script runs, in the configured default folder.
    ResetEnvironment(actualPath: drawingPath);
    string? copySeenByScript = null;
    RoslynExecutor.Handler = (code, _) =>
    {
      copySeenByScript = Directory.Exists(BackupDirectory)
        ? Directory.GetFiles(BackupDirectory).SingleOrDefault()
        : null;
      return Task.FromResult<object?>(code);
    };
    var written = JsonNode.Parse(await SendAsync(CreateSafetyWriteRequest(
      "backup-default", "backup-default", drawingPath, p => p["backup"] = true)))!.AsObject();
    Assert(written["result"]?.GetValue<string>() == "backup-default", "the write must run after the backup");
    var backupPath = written["receipt"]?["backupPath"]?.GetValue<string>();
    Assert(backupPath != null && backupPath == copySeenByScript, "the receipt must name the copy made before the script");
    Assert(Path.GetDirectoryName(backupPath) == BackupDirectory, "the default folder comes from the setting");
    Assert(Path.GetFileName(backupPath)!.StartsWith("Út terv.", StringComparison.Ordinal)
      && backupPath!.EndsWith(".dwg", StringComparison.Ordinal),
      "the copy keeps the drawing name and extension around a time stamp");
    Assert(File.ReadAllText(backupPath) == "saved drawing content", "the copy must equal the saved file");

    // A second copy in the same second gets its own name; nothing is overwritten.
    var first = DrawingBackup.Create(drawingPath, null, new DateTime(2026, 11, 11, 9, 30, 0));
    var second = DrawingBackup.Create(drawingPath, null, new DateTime(2026, 11, 11, 9, 30, 0));
    Assert(Path.GetFileName(first) == "Út terv.20261111-093000.dwg", "the stamp is fixed-format local time");
    Assert(Path.GetFileName(second) == "Út terv.20261111-093000-2.dwg", "a name that is taken is never reused");

    // A requested folder wins over the setting.
    ResetEnvironment(actualPath: drawingPath);
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    var requestedDirectory = Path.Combine(SafetyDirectory, "requested");
    var requested = JsonNode.Parse(await SendAsync(CreateSafetyWriteRequest(
      "backup-requested", "backup-requested", drawingPath, p =>
      {
        p["backup"] = true;
        p["backupDirectory"] = requestedDirectory;
      })))!.AsObject();
    Assert(Path.GetDirectoryName(requested["receipt"]?["backupPath"]?.GetValue<string>()) == requestedDirectory,
      "backupDirectory must choose the folder of the copy");

    // The copy is taken from the saved file, so unsaved changes refuse it by default.
    ResetEnvironment(actualPath: drawingPath);
    Application.DbMod = 1;
    var copiesBefore = Directory.GetFiles(BackupDirectory).Length;
    var dirtyBackup = await SendAsync(
      CreateSafetyWriteRequest("backup-dirty", "must-not-run", drawingPath, p => p["backup"] = true));
    AssertError(dirtyBackup, "CIVIL3D.DRAWING_NOT_CLEAN");
    var dirtyBackupMessage = JsonNode.Parse(dirtyBackup)!["error"]!["message"]!.GetValue<string>();
    Assert(dirtyBackupMessage.Contains("requireClean: false", StringComparison.Ordinal)
      && !dirtyBackupMessage.Contains("without requireClean", StringComparison.Ordinal),
      "the refusal must name the way out for a caller who only asked for a backup");
    AssertNoCivilResourcesOpened();
    Assert(Directory.GetFiles(BackupDirectory).Length == copiesBefore, "a refused call must not leave a copy");

    // The caller may knowingly take the copy of the last saved state anyway.
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    var knowingly = JsonNode.Parse(await SendAsync(CreateSafetyWriteRequest(
      "backup-dirty-allowed", "backup-dirty-allowed", drawingPath, p =>
      {
        p["backup"] = true;
        p["requireClean"] = false;
      })))!.AsObject();
    Assert(knowingly["receipt"]?["backupPath"] != null && knowingly["receipt"]?["dbmodBefore"]?.GetValue<int>() == 1,
      "an explicit requireClean=false keeps the backup and reports the unsaved state");

    // No file, no backup, no write.
    ResetEnvironment(actualPath: drawingPath, dwgTitled: false);
    AssertError(
      await SendAsync(CreateSafetyWriteRequest("backup-unnamed", "must-not-run", drawingPath, p => p["backup"] = true)),
      "CIVIL3D.SAVE_PATH_REQUIRED");
    AssertNoCivilResourcesOpened();

    var missingPath = Path.Combine(drawingDirectory, "missing.dwg");
    ResetEnvironment(actualPath: missingPath);
    var failed = await SendAsync(CreateSafetyWriteRequest(
      "backup-missing", "must-not-run", missingPath, p => p["backup"] = true));
    AssertError(failed, "CIVIL3D.BACKUP_FAILED");
    AssertNoCivilResourcesOpened();
    Assert(JsonNode.Parse(failed)!["receipt"]?["backupPath"] == null, "a failed copy must not be named");

    ResetEnvironment(actualPath: drawingPath);
    AssertError(
      await SendAsync(CreateSafetyWriteRequest("backup-relative", "must-not-run", drawingPath, p =>
      {
        p["backup"] = true;
        p["backupDirectory"] = "relative-folder";
      })),
      "CIVIL3D.INVALID_INPUT");
    AssertError(
      await SendAsync(CreateSafetyWriteRequest("backup-folder-only", "must-not-run", drawingPath,
        p => p["backupDirectory"] = requestedDirectory)),
      "CIVIL3D.INVALID_INPUT");
    AssertNoCivilResourcesOpened();
  }

  private static async Task WriteReceiptAsync()
  {
    ClearSafetyFiles();
    var drawingDirectory = Path.Combine(SafetyDirectory, "drawings");
    Directory.CreateDirectory(drawingDirectory);
    var drawingPath = Path.Combine(drawingDirectory, "Receipt.dwg");
    File.WriteAllText(drawingPath, "12345");

    ResetEnvironment(actualPath: drawingPath);
    RoslynExecutor.Handler = (code, _) =>
    {
      Application.DbMod = 1;
      return Task.FromResult<object?>(code);
    };
    var saved = JsonNode.Parse(await SendAsync(CreateRequest(
      "receipt-saved",
      readOnly: false,
      includeExpectedDrawing: true,
      expectedDrawing: Guard(drawingPath, ActualFingerprint),
      code: "receipt-saved",
      saveDrawing: true)))!.AsObject();
    Assert(saved["result"]?.GetValue<string>() == "receipt-saved", "the result keeps its own place and shape");
    var receipt = saved["receipt"]?.AsObject();
    Assert(receipt != null, "a write must return a receipt next to the result");
    Assert(receipt!["drawing"]?["databaseFilename"]?.GetValue<string>() == drawingPath
      && receipt["drawing"]?["fingerprintGuid"]?.GetValue<string>() == ActualFingerprint.ToString("B").ToUpperInvariant(),
      "the receipt names the drawing that was written");
    Assert(receipt["dbmodBefore"]?.GetValue<int>() == 0 && receipt["dbmodAfter"]?.GetValue<int>() == 1,
      "the receipt reports the unsaved-changes flag before and after");
    Assert(receipt["committed"]?.GetValue<bool>() == true && receipt["saved"]?.GetValue<bool>() == true,
      "the receipt reports commit and save");
    Assert(receipt["fileBytes"]?.GetValue<long>() == 5 && receipt["fileLastWriteUtc"] != null,
      "the receipt reports the file as it is on disk after the call");
    Assert(receipt["backupPath"] == null, "no backup was asked for");

    // Without saveDrawing the change is in memory only, and the receipt says so.
    ResetEnvironment(actualPath: drawingPath);
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    var memoryOnly = JsonNode.Parse(await SendAsync(CreateRequest(
      "receipt-memory",
      readOnly: false,
      includeExpectedDrawing: true,
      expectedDrawing: Guard(drawingPath, ActualFingerprint),
      code: "receipt-memory")))!["receipt"]?.AsObject();
    Assert(memoryOnly?["committed"]?.GetValue<bool>() == true && memoryOnly["saved"]?.GetValue<bool>() == false,
      "a committed but unsaved write must not claim a save");

    // A failed save keeps the committed fact visible.
    ResetEnvironment(actualPath: drawingPath);
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    CurrentDatabase.SaveAsException = new InvalidOperationException("disk full");
    var saveFailed = await SendAsync(CreateRequest(
      "receipt-save-failed",
      readOnly: false,
      includeExpectedDrawing: true,
      expectedDrawing: Guard(drawingPath, ActualFingerprint),
      code: "receipt-save-failed",
      saveDrawing: true));
    AssertError(saveFailed, "CIVIL3D.SAVE_FAILED");
    var failedReceipt = JsonNode.Parse(saveFailed)!["receipt"]?.AsObject();
    Assert(failedReceipt?["committed"]?.GetValue<bool>() == true && failedReceipt["saved"]?.GetValue<bool>() == false,
      "a save failure must report a committed, unsaved write");

    // A script error rolls back; the receipt says nothing was committed.
    ResetEnvironment(actualPath: drawingPath);
    RoslynExecutor.Handler = (_, _) => throw new InvalidOperationException("script failed");
    var scriptFailed = JsonNode.Parse(await SendAsync(CreateRequest(
      "receipt-script-failed",
      readOnly: false,
      includeExpectedDrawing: true,
      expectedDrawing: Guard(drawingPath, ActualFingerprint),
      code: "receipt-script-failed")))!.AsObject();
    Assert(scriptFailed["error"] != null && scriptFailed["receipt"]?["committed"]?.GetValue<bool>() == false,
      "a failed script must report an uncommitted write");

    // Queries change nothing, so they carry no receipt; neither does a call refused before the drawing.
    ResetEnvironment(actualPath: drawingPath);
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    var query = JsonNode.Parse(await SendAsync(CreateRequest("receipt-query", readOnly: true, code: "query")))!.AsObject();
    Assert(query["result"]?.GetValue<string>() == "query" && !query.ContainsKey("receipt"),
      "a query must not carry a receipt");
    var unguarded = JsonNode.Parse(await SendAsync(CreateRequest("receipt-unguarded", readOnly: false)))!.AsObject();
    Assert(unguarded["error"] != null && !unguarded.ContainsKey("receipt"),
      "a write refused before the drawing was looked at has nothing to report");
  }

  private static async Task WriteJournalAsync()
  {
    ClearSafetyFiles();
    ResetEnvironment();
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>("secret-result");
    const string secretCode = "var secret = 42; return secret;";
    AssertResult(
      await SendAsync(CreateSafetyWriteRequest("journal-ok", secretCode, ActualPath, p =>
      {
        p["description"] = "Tengelyek\r\nátnevezése   a 12-es úton";
        p["requireClean"] = true;
      })),
      "secret-result");

    Application.DbMod = 1;
    AssertError(
      await SendAsync(CreateSafetyWriteRequest("journal-refused", secretCode, ActualPath, p => p["requireClean"] = true)),
      "CIVIL3D.DRAWING_NOT_CLEAN");
    Application.DbMod = 0;

    // Queries are not journaled.
    AssertResult(await SendAsync(CreateRequest("journal-query", readOnly: true, code: "query")), "secret-result");

    var lines = ReadJournalLines();
    Assert(lines.Length == 2, $"exactly the two write requests must be journaled, found {lines.Length}");
    var ok = JsonNode.Parse(lines[0])!.AsObject();
    var refused = JsonNode.Parse(lines[1])!.AsObject();
    Assert(ok["ok"]?.GetValue<bool>() == true && ok["errorCode"] == null && ok["committed"]?.GetValue<bool>() == true,
      "a successful write is journaled as committed");
    Assert(ok["description"]?.GetValue<string>() == "Tengelyek átnevezése a 12-es úton",
      "the description is kept as one readable line");
    Assert(ok["drawing"]?.GetValue<string>() == ActualPath && ok["instanceId"]?.GetValue<string>() == PluginRuntime.InstanceId,
      "the line names the drawing and the Civil 3D session");
    Assert(ok["codeBytes"]?.GetValue<int>() == secretCode.Length
      && ok["codeSha256"]?.GetValue<string>() == Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secretCode))).ToLowerInvariant(),
      "the code is identified by size and hash");
    Assert(ok["timeUtc"] != null && ok["durationMs"] != null && ok["pluginCommit"]?.GetValue<string>() == BuildInfo.Commit,
      "the line carries time, duration and plugin build");
    Assert(refused["ok"]?.GetValue<bool>() == false
      && refused["errorCode"]?.GetValue<string>() == "CIVIL3D.DRAWING_NOT_CLEAN"
      && refused["committed"]?.GetValue<bool>() == false
      && refused["dbmodBefore"]?.GetValue<int>() == 1,
      "a refused write is journaled with its reason");
    Assert(lines.All(line => !line.Contains("secret", StringComparison.Ordinal)),
      "the journal must hold neither the code nor the result");

    // A journal that cannot be written never changes the answer of the write.
    var blocked = Path.Combine(SafetyDirectory, "blocked");
    File.WriteAllText(blocked, "a file where the folder should be");
    Environment.SetEnvironmentVariable(WriteJournal.DirectoryEnvironmentVariable, blocked);
    try
    {
      AssertResult(await SendAsync(CreateGuardedWriteRequest("journal-blocked", "code")), "secret-result");
    }
    finally
    {
      Environment.SetEnvironmentVariable(WriteJournal.DirectoryEnvironmentVariable, JournalDirectory);
    }
  }

  private static async Task DescriptionEchoAsync()
  {
    ResetEnvironment();
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    AssertResult(
      await SendAsync(CreateSafetyWriteRequest("echo", "echo", ActualPath,
        p => p["description"] = "Lejtés 2,5% {0}\nmásodik sor")),
      "echo");
    var message = CurrentDocument.Editor.Messages.Single(text => text.Contains("[Civil 3D MCP]", StringComparison.Ordinal));
    Assert(message.Trim() == "[Civil 3D MCP] Lejtés 2,5pct (0) második sor",
      $"the text must be one line without format characters, got: {message}");

    // No description, no message; a query never writes one.
    ResetEnvironment();
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    AssertResult(await SendAsync(CreateGuardedWriteRequest("echo-none", "echo-none")), "echo-none");
    var query = JsonNode.Parse(CreateRequest("echo-query", readOnly: true, code: "echo-query"))!.AsObject();
    query["params"]!.AsObject()["description"] = "only a query";
    AssertResult(await SendAsync(query.ToJsonString()), "echo-query");
    Assert(!CurrentDocument.Editor.Messages.Any(text => text.Contains("[Civil 3D MCP]", StringComparison.Ordinal)),
      "nothing is shown without a description or for a query");
  }

  private static async Task HealthActiveDrawingAsync()
  {
    ResetEnvironment();
    var commandContextsBefore = Application.DocumentManager.CommandContextCallCount;

    var unknown = await GetHealthAsync();
    Assert(unknown["activeDrawingKnown"]?.GetValue<bool>() == false && unknown["activeDrawing"] == null,
      "before the first look the drawing is unknown, not guessed");
    Assert(unknown["build"]?["commit"]?.GetValue<string>() == BuildInfo.Unknown
      && unknown["build"]?.AsObject().ContainsKey("builtAtUtc") == true
      && unknown["build"]?.AsObject().ContainsKey("dirty") == true,
      "a build without a stamp says so instead of inventing a commit");
    Assert(unknown["features"]?.AsArray().Select(item => item?.GetValue<string>()).SequenceEqual(
      new[] { "activeDrawing", "writeSafety" }) == true,
      "health lists what this plugin build can do");

    // The identity is picked up when the application is idle.
    ActiveDrawingTracker.Initialize();
    try
    {
      Application.RaiseIdle();
      var known = await GetHealthAsync();
      Assert(known["activeDrawingKnown"]?.GetValue<bool>() == true, "an idle moment must record the drawing");
      Assert(known["activeDrawing"]?["databaseFilename"]?.GetValue<string>() == ActualPath
        && known["activeDrawing"]?["fingerprintGuid"]?.GetValue<string>() == ActualFingerprint.ToString("B").ToUpperInvariant()
        && known["activeDrawing"]?["observedAtUtc"] != null,
        "health repeats the identity exactly as the drawing reports it");

      // Switching to another drawing shows up at the next look.
      const string otherPath = @"C:\Projects\Plans\Other.dwg";
      Application.DocumentManager.MdiActiveDocument = new Document(new Database
      {
        Filename = otherPath,
        FingerprintGuid = "{AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE}",
      });
      ActiveDrawingTracker.Refresh();
      var switched = await GetHealthAsync();
      Assert(switched["activeDrawing"]?["databaseFilename"]?.GetValue<string>() == otherPath,
        "a switched drawing must replace the remembered one");

      // No open drawing is a known state of its own.
      Application.DocumentManager.MdiActiveDocument = null;
      ActiveDrawingTracker.Refresh();
      var none = await GetHealthAsync();
      Assert(none["activeDrawingKnown"]?.GetValue<bool>() == true && none["activeDrawing"] == null,
        "no open drawing is reported as known and empty");
    }
    finally
    {
      ActiveDrawingTracker.Terminate();
    }

    // After Terminate the idle event is no longer followed.
    ResetEnvironment();
    Application.RaiseIdle();
    Assert((await GetHealthAsync())["activeDrawingKnown"]?.GetValue<bool>() == false,
      "a terminated tracker must not keep listening");

    // Every operation refreshes the identity when it ends, on the main thread.
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    AssertResult(await SendAsync(CreateRequest("health-after-query", readOnly: true, code: "query")), "query");
    var afterQuery = await GetHealthAsync();
    Assert(afterQuery["activeDrawing"]?["databaseFilename"]?.GetValue<string>() == ActualPath,
      "a finished operation leaves the identity current");
    Assert(Application.DocumentManager.CommandContextCallCount - commandContextsBefore <= 1,
      "only the query itself may enter the command context; health never does");
  }

  private static async Task ScriptTimeLimitParameterAsync()
  {
    static string WithTimeLimit(string request, JsonNode? value)
    {
      var parsed = JsonNode.Parse(request)!.AsObject();
      parsed["params"]!["timeoutSeconds"] = value;
      return parsed.ToJsonString();
    }

    ResetEnvironment();
    RoslynExecutor.Handler = (code, _) => Task.FromResult<object?>(code);
    AssertResult(await SendAsync(CreateGuardedWriteRequest("limit-default", "default")), "default");
    Assert(RoslynExecutor.LastTimeLimit == null, "no parameter must leave the executor default in place");

    AssertResult(
      await SendAsync(WithTimeLimit(CreateGuardedWriteRequest("limit-long", "long"), 900)),
      "long"
    );
    Assert(RoslynExecutor.LastTimeLimit == TimeSpan.FromSeconds(900), "the limit must reach the executor");

    AssertResult(
      await SendAsync(WithTimeLimit(CreateRequest("limit-query", readOnly: true, code: "query"), 1)),
      "query"
    );
    Assert(RoslynExecutor.LastTimeLimit == TimeSpan.FromSeconds(1), "queries accept the limit too");

    foreach (var invalid in new JsonNode?[] { 0, -5, 1801, 1.5, "600", true })
    {
      ResetEnvironment();
      AssertError(
        await SendAsync(WithTimeLimit(CreateGuardedWriteRequest("limit-invalid", "must-not-run"), invalid)),
        "CIVIL3D.INVALID_INPUT"
      );
      AssertNoCivilResourcesOpened();
    }
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

  private static async Task StaleEndpointRecordsAsync()
  {
    ResetEnvironment();
    Directory.CreateDirectory(EndpointDirectory);
    foreach (var leftover in Directory.GetFiles(EndpointDirectory)) File.Delete(leftover);

    // A process id that existed a moment ago and is gone now.
    int deadProcessId;
    using (var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
      FileName = "dotnet",
      Arguments = "--version",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    })!)
    {
      deadProcessId = child.Id;
      await child.StandardOutput.ReadToEndAsync();
      await child.WaitForExitAsync();
    }

    var now = DateTimeOffset.UtcNow;
    Assert(!EndpointRegistration.IsRecordedProcessRunning(deadProcessId, now),
      "a process that is gone must not count as running");
    Assert(EndpointRegistration.IsRecordedProcessRunning(Environment.ProcessId, now),
      "a record written by a running process must count as running");
    Assert(!EndpointRegistration.IsRecordedProcessRunning(Environment.ProcessId, now.AddDays(-30)),
      "a process that started after the record was written only reuses the id");
    // Windows may hand the id of a closed Civil 3D to another program whose
    // start time cannot be read; its name still tells that the id was reused.
    using (var self = System.Diagnostics.Process.GetCurrentProcess())
    {
      Assert(EndpointRegistration.IsRecordedProcessRunning(Environment.ProcessId, now, self.ProcessName),
        "a record of the same program must count as running");
      Assert(EndpointRegistration.IsRecordedProcessRunning(Environment.ProcessId, now, self.ProcessName.ToUpperInvariant()),
        "the program name is compared without regard to case");
    }
    Assert(!EndpointRegistration.IsRecordedProcessRunning(Environment.ProcessId, now, "some-other-program"),
      "an id that now belongs to another program was reused");

    string Record(int processId, string instanceId) => new JsonObject
    {
      ["schemaVersion"] = EndpointRegistration.SchemaVersion,
      ["instanceId"] = instanceId,
      ["processId"] = processId,
      ["port"] = 48999,
      ["startedAtUtc"] = now.ToString("o"),
    }.ToJsonString();

    var dead = Path.Combine(EndpointDirectory, "1-dead.json");
    var own = Path.Combine(EndpointDirectory, "2-own.json");
    var foreign = Path.Combine(EndpointDirectory, "3-foreign.json");
    var other = Path.Combine(EndpointDirectory, "4-notes.txt");
    File.WriteAllText(dead, Record(deadProcessId, new string('a', 32)));
    File.WriteAllText(own, Record(Environment.ProcessId, new string('b', 32)));
    File.WriteAllText(foreign, "not json");
    File.WriteAllText(other, "keep");

    Assert(EndpointRegistration.RemoveStaleRecords(EndpointDirectory) == 1, "exactly the dead record is stale");
    Assert(!File.Exists(dead), "the record of a process that is gone must be removed");
    Assert(File.Exists(own), "a record of a running process must stay");
    Assert(File.Exists(foreign) && File.Exists(other), "files that are not endpoint records must stay");

    File.WriteAllText(dead, Record(deadProcessId, new string('a', 32)));
    try
    {
      PluginRuntime.StartServer();
      Assert(!File.Exists(dead), "starting a session must clean up dead records");
      Assert(Directory.GetFiles(EndpointDirectory, "*.json").Length == 3,
        "the new session's record must be published next to the surviving ones");
    }
    finally
    {
      PluginRuntime.StopServer();
      foreach (var path in new[] { dead, own, foreign, other })
      {
        if (File.Exists(path)) File.Delete(path);
      }
    }
  }

  private static async Task HealthBypassesSerializedQueueAsync()
  {
    using var diagnostics = SetCompletionDiagnostics(null);
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
        "completionDiagnostics",
        "lastCompletionDiagnostics",
        "queueDepth",
        "instanceId",
        "processId",
        "port",
        "startedAtUtc",
        "mode",
        "roslyn",
        "executionBackend",
        "activeDrawingKnown",
        "activeDrawing",
        "build",
        "features",
      };
      Assert(health!.Count == expectedFields.Count && health.All(item => expectedFields.Contains(item.Key)),
        "health response must remain limited to the stable status fields");
      Assert(health["connected"]?.GetValue<bool>() == true, "health must report the live connection");
      Assert(health["listenerRunning"]?.GetValue<bool>() == true, "health must report the listener");
      Assert(health["executionBackend"]?.GetValue<string>() == (_modalHost == null ? "native" : "modal"),
        "shared contracts must exercise the selected execution backend");
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
      Assert(health["completionDiagnostics"] == null && health["lastCompletionDiagnostics"] == null,
        "health must leave completion diagnostics null until explicitly enabled");
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
      Assert(health["activeDrawingKnown"]?.GetValue<bool>() == false && health["activeDrawing"] == null,
        "health must only repeat a remembered drawing identity and never read one itself");

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

  private static IDisposable SetCompletionDiagnostics(string? value)
    => new EnvironmentVariableScope(
      OperationProgress.CompletionDiagnosticsEnvironmentVariable,
      value
    );

  private sealed class EnvironmentVariableScope : IDisposable
  {
    private readonly string _name;
    private readonly string? _previousValue;
    private bool _disposed;

    public EnvironmentVariableScope(string name, string? value)
    {
      _name = name;
      _previousValue = Environment.GetEnvironmentVariable(name);
      Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
      if (_disposed) return;
      _disposed = true;
      Environment.SetEnvironmentVariable(_name, _previousValue);
    }
  }

  private static void ResetEnvironment(
    string actualPath = ActualPath,
    bool? dwgTitled = null)
  {
    CivilExecution.UseNativeBackendOverrideForTests = _modalHost == null;
    Application.DocumentManager.BeforeCommandContextAsync = null;
    Application.DocumentManager.AfterCommandContextAsync = null;
    Application.DocumentManager.CommandContextScheduleException = null;
    Application.DocumentManager.CommandContextCompletionException = null;
    Application.DocumentManager.CommandContextScheduleOverrideAsync = null;
    Application.DocumentManager.CompleteDuringOnCompletedRegistration = false;
    Application.DocumentManager.SuppressCompletionContinuation = false;
    CommandContextAdmission.StartDeadlineOverrideForTests = null;
    CommandContextAdmission.LateFaultObserverForTests = null;
    CommandContextAdmission.NativeSampleOffsetsOverrideForTests = null;
    AssertCleanStatus();
    PluginRuntime.ResetIdempotencyForTests();
    var database = new Database
    {
      Filename = actualPath,
      FingerprintGuid = ActualFingerprint.ToString("B").ToUpperInvariant(),
    };
    Application.DocumentManager.MdiActiveDocument = new Document(database);
    _modalHost?.Attach(CurrentDocument);
    Application.DocumentManager.CommandContextDelay = TimeSpan.Zero;
    Application.DwgTitled = (dwgTitled ?? !string.IsNullOrEmpty(actualPath)) ? 1 : 0;
    Application.DbMod = 0;
    ActiveDrawingTracker.ResetForTests();
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
