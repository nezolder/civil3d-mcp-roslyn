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

  public static async Task Main()
  {
    var tests = new (string Name, Func<Task> Run)[]
    {
      ("direct execute guard is required and malformed guards fail closed", DirectGuardContractAsync),
      ("drawing identity is checked before lock, transaction, and Roslyn", DrawingIdentityOrderingAsync),
      ("failed listener start does not report running", FailedListenerStartDoesNotReportRunningAsync),
      ("parallel dispatch is serialized with exact waiting and active status", SerializedDispatchStatusAsync),
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

    foreach (var (name, run) in tests)
    {
      await run();
      Console.WriteLine($"PASS {name}");
    }

    ResultSerializerTests.RunAll();
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
        "queueDepth",
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
      Assert(health["queueDepth"]?.GetValue<int>() == 1,
        "health must report the waiting executeCode request only");
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

  private static void ResetEnvironment(string actualPath = ActualPath)
  {
    AssertCleanStatus();
    PluginRuntime.ResetIdempotencyForTests();
    var database = new Database
    {
      Filename = actualPath,
      FingerprintGuid = ActualFingerprint.ToString("B").ToUpperInvariant(),
    };
    Application.DocumentManager.MdiActiveDocument = new Document(database);
    Application.DocumentManager.CommandContextDelay = TimeSpan.Zero;
    CivilApplication.ActiveDocument = new CivilDocument();
    RoslynExecutor.Reset();
  }

  private static JsonObject Guard(string databaseFilename, Guid fingerprintGuid)
    => new()
    {
      ["databaseFilename"] = databaseFilename,
      ["fingerprintGuid"] = fingerprintGuid.ToString("D"),
    };

  private static string CreateGuardedWriteRequest(string id, string code)
    => CreateRequest(
      id,
      readOnly: false,
      includeExpectedDrawing: true,
      expectedDrawing: Guard(ActualPath, ActualFingerprint),
      code: code
    );

  private static string CreateIdempotentWriteRequest(string id, JsonNode? idempotencyKey, string code)
  {
    var request = JsonNode.Parse(CreateGuardedWriteRequest(id, code))!.AsObject();
    request["params"]!.AsObject()["idempotencyKey"] = idempotencyKey?.DeepClone();
    return request.ToJsonString();
  }

  private static string CreateIdempotentWriteRequest(string id, string idempotencyKey, string code)
    => CreateIdempotentWriteRequest(id, JsonValue.Create(idempotencyKey), code);

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
    string code = "return-value")
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
