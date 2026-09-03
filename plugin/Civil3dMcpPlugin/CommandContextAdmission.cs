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

  internal static async Task ExecuteAsync(
    DocumentCollection documentManager,
    Func<object?, Task> callback,
    object? userData,
    CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();

    var admission = new Admission(callback);
    var schedulingTask = ScheduleAsync(documentManager, admission.InvokeAsync, userData);
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
    object? userData)
  {
    // Civil 3D 2025's ExecutionResult has a non-race-safe awaiter: completion
    // can occur after compiler await reads IsCompleted but before it registers
    // OnCompleted. Bridge the native completion explicitly and double-check.
    var awaiter = documentManager.ExecuteInCommandContextAsync(callback, userData).GetAwaiter();
    if (awaiter.IsCompleted)
    {
      awaiter.GetResult();
      return;
    }

    var nativeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void SignalNativeCompletion() => nativeCompletion.TrySetResult();
    awaiter.OnCompleted(SignalNativeCompletion);
    Thread.MemoryBarrier();
    if (awaiter.IsCompleted)
    {
      SignalNativeCompletion();
    }

    await nativeCompletion.Task.ConfigureAwait(false);
    awaiter.GetResult();
  }

  private sealed class Admission
  {
    private const int Pending = 0;
    private const int Started = 1;
    private const int Abandoned = 2;

    private int _state;
    private Func<object?, Task>? _callback;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Admission(Func<object?, Task> callback)
    {
      _callback = callback;
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
      return callback == null ? Task.CompletedTask : callback(userData);
    }

    public void ClearCallback() => Interlocked.Exchange(ref _callback, null);
  }
}
