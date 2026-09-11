using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Bounds only admission to AutoCAD's command context. Once the callback has
/// started, its original Autodesk task remains authoritative and is awaited by
/// the caller; this helper never preempts Civil work.
/// </summary>
internal static class CommandContextAdmission
{
  private static readonly TimeSpan ProductionStartDeadline = TimeSpan.FromSeconds(15);
  private static TimeSpan? _startDeadlineOverrideForTests;

  // Test-only seam; production always uses the fixed deadline above.
  internal static TimeSpan? StartDeadlineOverrideForTests
  {
    get => _startDeadlineOverrideForTests;
    set => _startDeadlineOverrideForTests = value;
  }

  // Test-only observation hook. Production keeps this null and does not log.
  internal static Action<Exception>? LateFaultObserverForTests { get; set; }

  private static readonly TimeSpan[] NativeSampleOffsets =
    [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5),
     TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
     TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300)];
  internal static TimeSpan[]? NativeSampleOffsetsOverrideForTests { get; set; }

  internal static async Task ExecuteAsync(
    DocumentCollection documentManager,
    Func<object?, Task> callback,
    object? userData,
    CancellationToken cancellationToken,
    OperationProgress? progress = null)
  {
    cancellationToken.ThrowIfCancellationRequested();

    var admission = new Admission(callback, progress);
    // Install before the Autodesk scheduling call. The observer is opt-in and
    // disposes on every normal exit; its own deadline covers a hung awaitable.
    using var firstChanceObserver = FirstChanceCompletionObserver.Start(progress);
    // This token stops opt-in diagnostics only. It never cancels admitted Civil work.
    var diagnosticStop = progress?.CompletionDiagnosticsEnabled == true
      ? new CancellationTokenSource()
      : null;
    var schedulingTask = ScheduleAsync(
      documentManager, admission.InvokeAsync, userData, progress,
      diagnosticStop?.Token ?? CancellationToken.None);
    using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    try
    {
      if (admission.HasStarted)
      {
        await schedulingTask;
        return;
      }

      // One scoped delay represents either the fixed deadline or server
      // cancellation. Disposing this CTS removes its registration on every
      // successful started request.
      var deadlineTask = Task.Delay(GetStartDeadline(), waitCancellation.Token);
      var completed = await Task.WhenAny(
        admission.StartedTask,
        schedulingTask,
        deadlineTask
      );

      // A callback that won the admission race must keep the original task
      // and the serialized gate alive, irrespective of deadline/cancellation.
      if (admission.HasStarted)
      {
        await schedulingTask;
        return;
      }

      if (ReferenceEquals(completed, schedulingTask))
      {
        // Mark pending work abandoned before surfacing a scheduling failure.
        // A native callback that arrives after a fault must remain a no-op.
        if (admission.TryAbandon())
        {
          await schedulingTask;
          throw NotStartedTimeout(deadlineElapsed: false);
        }
        await schedulingTask;
        return;
      }

      if (admission.TryAbandon())
      {
        ObserveLateFault(schedulingTask);
        if (cancellationToken.IsCancellationRequested)
        {
          throw new OperationCanceledException(cancellationToken);
        }
        throw NotStartedTimeout(deadlineElapsed: true);
      }

      // Started concurrently with deadline/cancellation. Never abandon work
      // after its callback has begun.
      await schedulingTask;
    }
    finally
    {
      diagnosticStop?.Cancel();
      diagnosticStop?.Dispose();
      progress?.RecordSamplingState(NativeSamplingState.Stopped);
      waitCancellation.Cancel();
      if (schedulingTask.IsCompleted)
      {
        admission.ClearCallback();
      }
    }
  }

  private static TimeSpan GetStartDeadline()
    => StartDeadlineOverrideForTests ?? ProductionStartDeadline;

  private static JsonRpcDispatchException NotStartedTimeout(bool deadlineElapsed)
    => new(
      "CIVIL3D.COMMAND_CONTEXT_TIMEOUT",
      deadlineElapsed
        ? "Civil 3D command context was not entered within 15 seconds; the script did not run. Open a drawing or finish active commands before manually trying again."
        : "Civil 3D command-context scheduling completed without callback admission; the script did not run. Open a drawing or finish active commands before manually trying again."
    );

  private static void ObserveLateFault(Task task)
  {
    _ = task.ContinueWith(
      static completed =>
      {
        var exception = completed.Exception;
        if (exception != null)
        {
          LateFaultObserverForTests?.Invoke(exception);
        }
      },
      CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default
    );
  }

  private static async Task ScheduleAsync(
    DocumentCollection documentManager,
    Func<object?, Task> callback,
    object? userData,
    OperationProgress? progress,
    CancellationToken diagnosticStop)
  {
    // Civil 3D 2025's ExecutionResult has a non-race-safe awaiter: completion
    // can occur after compiler await reads IsCompleted but before it registers
    // OnCompleted. Bridge the native completion explicitly and double-check.
    var awaiter = documentManager.ExecuteInCommandContextAsync(callback, userData).GetAwaiter();
    void GetNativeResult()
    {
      progress?.RecordGetResult(null);
      try
      {
        awaiter.GetResult();
        progress?.RecordGetResult(true);
      }
      catch
      {
        progress?.RecordGetResult(false);
        throw;
      }
    }

    var initiallyCompleted = awaiter.IsCompleted;
    progress?.RecordNativeIsCompleted(initiallyCompleted, afterRegistration: false);
    if (initiallyCompleted)
    {
      GetNativeResult();
      return;
    }

    var nativeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void SignalNativeCompletion() => nativeCompletion.TrySetResult();
    void OnNativeContinuation()
    {
      progress?.RecordContinuationInvocation();
      SignalNativeCompletion();
    }
    awaiter.OnCompleted(OnNativeContinuation);
    progress?.RecordContinuationRegistration();
    Thread.MemoryBarrier();
    var completedAfterRegistration = awaiter.IsCompleted;
    progress?.RecordNativeIsCompleted(completedAfterRegistration, afterRegistration: true);
    if (completedAfterRegistration)
    {
      SignalNativeCompletion();
    }

    CancellationTokenSource? observerStop = null;
    if (progress?.CompletionDiagnosticsEnabled == true && !nativeCompletion.Task.IsCompleted)
    {
      // Observation only: seeing true here MUST NOT signal nativeCompletion.
      // The 2025 getter reads managed completion state, not drawing objects.
      observerStop = CancellationTokenSource.CreateLinkedTokenSource(diagnosticStop);
      _ = SampleNativeCompletionAsync(() => awaiter.IsCompleted, progress, observerStop.Token);
    }
    try
    {
      await nativeCompletion.Task.ConfigureAwait(false);
    }
    finally
    {
      observerStop?.Cancel();
      observerStop?.Dispose();
      progress?.RecordSamplingState(NativeSamplingState.Stopped);
    }
    GetNativeResult();
  }

  internal static async Task SampleNativeCompletionAsync(
    Func<bool> isCompleted,
    OperationProgress progress,
    CancellationToken cancellationToken)
  {
    progress.RecordSamplingState(NativeSamplingState.Running);
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    try
    {
      foreach (var offset in NativeSampleOffsetsOverrideForTests ?? NativeSampleOffsets)
      {
        var remaining = offset - System.Diagnostics.Stopwatch.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero)
          await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        progress.RecordNativeSample(isCompleted());
      }
      progress.RecordSamplingState(NativeSamplingState.Exhausted);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      progress.RecordSamplingState(NativeSamplingState.Stopped);
    }
    catch
    {
      // Diagnostics must neither throw into execution nor retain exception data.
      progress.RecordSamplingState(NativeSamplingState.ReadFailed);
    }
  }

  private sealed class Admission
  {
    private const int Pending = 0;
    private const int Started = 1;
    private const int Abandoned = 2;

    private int _state;
    private Func<object?, Task>? _callback;
    private readonly OperationProgress? _progress;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Admission(Func<object?, Task> callback, OperationProgress? progress)
    {
      _callback = callback;
      _progress = progress;
    }

    public Task StartedTask => _started.Task;
    public bool HasStarted => Volatile.Read(ref _state) == Started;

    public bool TryAbandon()
    {
      if (Interlocked.CompareExchange(ref _state, Abandoned, Pending) != Pending)
      {
        return false;
      }
      ClearCallback();
      return true;
    }

    public Task InvokeAsync(object? userData)
    {
      if (Interlocked.CompareExchange(ref _state, Started, Pending) != Pending)
      {
        ClearCallback();
        return Task.CompletedTask;
      }

      var callback = Interlocked.Exchange(ref _callback, null);
      _started.TrySetResult();
      if (_progress?.CompletionDiagnosticsEnabled != true)
      {
        return callback == null ? Task.CompletedTask : callback(userData);
      }

      var synchronizationContextBefore = DescribeSynchronizationContext(SynchronizationContext.Current);
      // Preserve the callback's exact return value, including a malformed
      // null. Command-context behavior owns what follows; diagnostics only
      // observe the scalar status and must not mask it with CompletedTask.
      Task? returnedTask = callback == null ? Task.CompletedTask : callback(userData);
      _progress.RecordReturnedCallback(
        synchronizationContextBefore,
        DescribeSynchronizationContext(SynchronizationContext.Current),
        DescribeTaskStatus(returnedTask),
        DescribeTaskScheduler(TaskScheduler.Current)
      );
      return returnedTask!;
    }

    public void ClearCallback() => Interlocked.Exchange(ref _callback, null);

    private static string DescribeSynchronizationContext(SynchronizationContext? context)
    {
      if (context == null) return "Null";
      // This local Civil 3D 2025 reference identifies the runtime context by
      // this exact name. Classification reads its exact runtime type name;
      // it invokes no context callback or Post operation.
      return string.Equals(
        context.GetType().FullName,
        "Autodesk.AutoCAD.Runtime.SynchronizationContext",
        StringComparison.Ordinal
      )
        ? "AutodeskAutoCAD"
        : "Other";
    }

    private static string DescribeTaskStatus(Task? task)
    {
      if (task == null) return "Null";
      return task.Status switch
      {
        TaskStatus.Created => "Created",
        TaskStatus.WaitingForActivation => "WaitingForActivation",
        TaskStatus.WaitingToRun => "WaitingToRun",
        TaskStatus.Running => "Running",
        TaskStatus.WaitingForChildrenToComplete => "WaitingForChildrenToComplete",
        TaskStatus.RanToCompletion => "RanToCompletion",
        TaskStatus.Canceled => "Canceled",
        TaskStatus.Faulted => "Faulted",
        _ => "Unknown",
      };
    }

    private static string DescribeTaskScheduler(TaskScheduler scheduler)
      => ReferenceEquals(scheduler, TaskScheduler.Default) ? "DefaultThreadPool" : "Other";
  }
}
