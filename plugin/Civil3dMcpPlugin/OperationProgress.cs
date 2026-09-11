using System.Diagnostics;

namespace Civil3DMcpPlugin;

// Fixed, data-free markers: never put drawing names, paths, code or results here.
internal enum OperationStage
{
  Dispatching,
  WaitingForCommandContext,
  ResolvingDrawing,
  AcquiringDocumentLock,
  StartingTransaction,
  PreparingScript,
  CompilingScript,
  RunningScript,
  SerializingResult,
  CommittingTransaction,
  DisposingTransaction,
  DisposingDocumentLock,
  SavingDrawing,
  WaitingForCommandContextCompletion,
  ReturningResult,
}

internal sealed record OperationProgressSnapshot(
  string Stage,
  double OperationElapsedMs,
  double StageElapsedMs,
  CommandContextDiagnosticSnapshot? CompletionDiagnostics
);

// Fixed scalars only. This private-health DTO never contains exceptions, code,
// document identities or user data. Snapshots are immutable once returned.
public sealed record CommandContextDiagnosticSnapshot
{
  public double? CallbackEnteredElapsedMs { get; init; }
  public double? CallbackExitedElapsedMs { get; init; }
  public string? CallbackSynchronizationContextBefore { get; init; }
  public string? CallbackSynchronizationContextAfter { get; init; }
  public string? ReturnedCallbackTaskStatus { get; init; }
  public string? CallbackReturnTaskScheduler { get; init; }
  public double? ReturnedCallbackTaskObservedElapsedMs { get; init; }
  public int MatchingFirstChanceExceptionCount { get; init; }
  public string FirstChanceObserverState { get; init; } = "NotStarted";
  public double? FirstMatchingFirstChanceExceptionElapsedMs { get; init; }
  public string? FirstMatchingFirstChanceExceptionCategory { get; init; }
  public string? FirstMatchingSynchronizationContext { get; init; }
  public string? FirstMatchingTaskScheduler { get; init; }
  public bool? FirstMatchingStackHasExecutionResultContinuation { get; init; }
  public bool? FirstMatchingStackHasSynchronizationContextPost { get; init; }
  public bool? CapturedException { get; init; }
  public string CommitOutcome { get; init; } = "NotApplicable";
  public string SaveOutcome { get; init; } = "NotRequested";
  public bool? InitialNativeIsCompleted { get; init; }
  public bool? PostRegistrationNativeIsCompleted { get; init; }
  public double? ContinuationRegisteredElapsedMs { get; init; }
  public int ContinuationInvocations { get; init; }
  public double? FirstContinuationElapsedMs { get; init; }
  public string GetResultOutcome { get; init; } = "NotStarted";
  public double? GetResultStartedElapsedMs { get; init; }
  public double? GetResultFinishedElapsedMs { get; init; }
  public int SampleCount { get; init; }
  public bool? LastSampledNativeIsCompleted { get; init; }
  public double? LastSampleElapsedMs { get; init; }
  public double? FirstSampledCompleteElapsedMs { get; init; }
  public string SamplingState { get; init; } = "NotStarted";
}

internal enum NativeSamplingState { Running, Stopped, Exhausted, ReadFailed }

/// <summary>
/// An operation-scoped, Civil-API-free snapshot for diagnosing a pending call.
/// Recording progress never cancels work or releases the serialized gate.
/// </summary>
internal sealed class OperationProgress
{
  internal const string CompletionDiagnosticsEnvironmentVariable =
    "CIVIL3D_MCP_COMPLETION_DIAGNOSTICS";

  private readonly object _sync = new();
  private readonly bool _completionDiagnosticsEnabled;
  private readonly long _startedAt = Stopwatch.GetTimestamp();
  private long _stageStartedAt;
  private OperationStage _stage = OperationStage.Dispatching;
  private CommandContextDiagnosticSnapshot _completion = null!;
  private int _firstChanceRecorded;
  private int _firstChanceObserverState;
  private int _firstChanceCategory;
  private int _firstChanceSynchronizationContext;
  private int _firstChanceTaskScheduler;
  private int _firstChanceStackFlags;
  private long _firstChanceTimestamp;

  public OperationProgress() : this(string.Equals(
    Environment.GetEnvironmentVariable(CompletionDiagnosticsEnvironmentVariable),
    "1",
    StringComparison.Ordinal
  )) { }

  // Host-free seam. Production captures the process setting once per operation.
  internal OperationProgress(bool completionDiagnosticsEnabled)
  {
    _completionDiagnosticsEnabled = completionDiagnosticsEnabled;
    if (completionDiagnosticsEnabled)
    {
      _completion = new CommandContextDiagnosticSnapshot();
    }
    _stageStartedAt = _startedAt;
  }

  public bool CompletionDiagnosticsEnabled => _completionDiagnosticsEnabled;

  private double ElapsedMs => Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;

  public void ConfigureExecution(bool write, bool saveDrawing)
  {
    if (!_completionDiagnosticsEnabled) return;
    lock (_sync) _completion = _completion with
    {
      CommitOutcome = write ? "NotStarted" : "NotApplicable",
      SaveOutcome = saveDrawing ? "NotStarted" : "NotRequested",
    };
  }

  public void RecordCallbackEntered()
  {
    if (!_completionDiagnosticsEnabled) return;
    lock (_sync) _completion = _completion with { CallbackEnteredElapsedMs = ElapsedMs };
  }

  public void RecordCallbackExited(bool capturedException)
  {
    if (!_completionDiagnosticsEnabled) return;
    lock (_sync) _completion = _completion with
    {
      CallbackExitedElapsedMs = ElapsedMs,
      CapturedException = capturedException,
    };
  }

  public void RecordReturnedCallback(
    string synchronizationContextBefore,
    string synchronizationContextAfter,
    string taskStatus,
    string taskScheduler)
  {
    if (!_completionDiagnosticsEnabled) return;
    lock (_sync) _completion = _completion with
    {
      CallbackSynchronizationContextBefore = synchronizationContextBefore,
      CallbackSynchronizationContextAfter = synchronizationContextAfter,
      ReturnedCallbackTaskStatus = taskStatus,
      CallbackReturnTaskScheduler = taskScheduler,
      ReturnedCallbackTaskObservedElapsedMs = ElapsedMs,
    };
  }

  // Called from AppDomain.FirstChanceException. Keep this lock-free and retain
  // only the first matching, fixed-category event.
  public void RecordMatchingFirstChanceException(
    int category,
    int synchronizationContext,
    int taskScheduler,
    bool hasContinuation,
    bool hasPost)
  {
    if (!_completionDiagnosticsEnabled ||
      Interlocked.CompareExchange(ref _firstChanceCategory, category, 0) != 0) return;
    Volatile.Write(ref _firstChanceStackFlags, (hasContinuation ? 1 : 0) | (hasPost ? 2 : 0));
    Volatile.Write(ref _firstChanceSynchronizationContext, synchronizationContext);
    Volatile.Write(ref _firstChanceTaskScheduler, taskScheduler);
    Volatile.Write(ref _firstChanceTimestamp, Stopwatch.GetTimestamp());
    Volatile.Write(ref _firstChanceRecorded, 1);
  }

  // Called from the process-global observer. Keep these lock-free as well.
  public void RecordFirstChanceObserverStarted()
  {
    if (_completionDiagnosticsEnabled)
      Interlocked.CompareExchange(ref _firstChanceObserverState, 1, 0);
  }

  public void RecordFirstChanceObserverTerminalState(int state)
  {
    if (_completionDiagnosticsEnabled) Interlocked.Exchange(ref _firstChanceObserverState, state);
  }

  private static string Outcome(bool? succeeded)
    => succeeded is null ? "Started" : succeeded.Value ? "Succeeded" : "Threw";

  public void RecordCommit(bool? succeeded)
  {
    if (!_completionDiagnosticsEnabled) return;
    lock (_sync) _completion = _completion with { CommitOutcome = Outcome(succeeded) };
  }

  public void RecordSave(bool? succeeded)
  {
    if (!_completionDiagnosticsEnabled) return;
    lock (_sync) _completion = _completion with { SaveOutcome = Outcome(succeeded) };
  }

  public void RecordNativeIsCompleted(bool completed, bool afterRegistration)
  {
    if (!_completionDiagnosticsEnabled) return;
    lock (_sync) _completion = afterRegistration
      ? _completion with { PostRegistrationNativeIsCompleted = completed }
      : _completion with { InitialNativeIsCompleted = completed };
  }

  public void RecordContinuationRegistration()
  {
    if (!_completionDiagnosticsEnabled) return;
    lock (_sync) _completion = _completion with { ContinuationRegisteredElapsedMs = ElapsedMs };
  }

  public void RecordContinuationInvocation()
  {
    if (!_completionDiagnosticsEnabled) return;
    lock (_sync) _completion = _completion with
    {
      ContinuationInvocations = _completion.ContinuationInvocations + 1,
      FirstContinuationElapsedMs = _completion.FirstContinuationElapsedMs ?? ElapsedMs,
    };
  }

  public void RecordGetResult(bool? succeeded)
  {
    if (!_completionDiagnosticsEnabled) return;
    lock (_sync) _completion = _completion with
    {
      GetResultOutcome = Outcome(succeeded),
      GetResultStartedElapsedMs = _completion.GetResultStartedElapsedMs ?? ElapsedMs,
      GetResultFinishedElapsedMs = succeeded is null ? null : ElapsedMs,
    };
  }

  public void RecordNativeSample(bool completed)
  {
    if (!_completionDiagnosticsEnabled) return;
    lock (_sync)
    {
      var elapsed = ElapsedMs;
      _completion = _completion with
      {
        SampleCount = _completion.SampleCount + 1,
        LastSampledNativeIsCompleted = completed,
        LastSampleElapsedMs = elapsed,
        FirstSampledCompleteElapsedMs = _completion.FirstSampledCompleteElapsedMs
          ?? (completed ? elapsed : null),
      };
    }
  }

  public void RecordSamplingState(NativeSamplingState state)
  {
    if (!_completionDiagnosticsEnabled) return;
    lock (_sync)
    {
      // Completion/cancellation cannot hide an exhausted or failed observer.
      if (state == NativeSamplingState.Stopped && _completion.SamplingState != "Running") return;
      _completion = _completion with { SamplingState = state.ToString() };
    }
  }

  public void SetStage(OperationStage stage)
  {
    lock (_sync)
    {
      _stage = stage;
      _stageStartedAt = Stopwatch.GetTimestamp();
    }
  }

  public OperationProgressSnapshot GetStatus()
  {
    lock (_sync)
    {
      var now = Stopwatch.GetTimestamp();
      var completion = _completionDiagnosticsEnabled ? WithFirstChance(_completion) : null;
      return new OperationProgressSnapshot(
        _stage.ToString(),
        Stopwatch.GetElapsedTime(_startedAt, now).TotalMilliseconds,
        Stopwatch.GetElapsedTime(_stageStartedAt, now).TotalMilliseconds,
        completion
      );
    }
  }

  private CommandContextDiagnosticSnapshot WithFirstChance(CommandContextDiagnosticSnapshot snapshot)
  {
    var observerState = Volatile.Read(ref _firstChanceObserverState) switch
    {
      1 => "Running",
      2 => "Matched",
      3 => "Expired",
      4 => "Stopped",
      5 => "Failed",
      _ => "NotStarted",
    };
    if (Volatile.Read(ref _firstChanceRecorded) == 0)
    {
      return observerState == "NotStarted" ? snapshot : snapshot with
      {
        FirstChanceObserverState = observerState,
      };
    }
    var flags = Volatile.Read(ref _firstChanceStackFlags);
    return snapshot with
    {
      FirstChanceObserverState = observerState,
      MatchingFirstChanceExceptionCount = 1,
      FirstMatchingFirstChanceExceptionElapsedMs =
        Stopwatch.GetElapsedTime(_startedAt, Volatile.Read(ref _firstChanceTimestamp)).TotalMilliseconds,
      FirstMatchingFirstChanceExceptionCategory = Volatile.Read(ref _firstChanceCategory) switch
      {
        1 => "InvalidOperation",
        2 => "NullReference",
        3 => "TaskScheduler",
        _ => "Other",
      },
      FirstMatchingSynchronizationContext = Volatile.Read(ref _firstChanceSynchronizationContext) switch
      {
        1 => "Null",
        2 => "AutodeskAutoCAD",
        _ => "Other",
      },
      FirstMatchingTaskScheduler = Volatile.Read(ref _firstChanceTaskScheduler) switch
      {
        1 => "DefaultThreadPool",
        _ => "Other",
      },
      FirstMatchingStackHasExecutionResultContinuation = (flags & 1) != 0,
      FirstMatchingStackHasSynchronizationContextPost = (flags & 2) != 0,
    };
  }
}
