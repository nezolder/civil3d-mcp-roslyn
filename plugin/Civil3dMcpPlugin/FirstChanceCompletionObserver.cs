using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Opt-in, process-global first-chance observation for the one Autodesk
/// completion path under investigation. It never handles, rethrows, or changes
/// exceptions; absence of an event is not completion evidence.
/// </summary>
internal sealed class FirstChanceCompletionObserver : IDisposable
{
  private const string ExecutionResultType =
    "Autodesk.AutoCAD.ApplicationServices.DocumentCollection+ExecutionResult";
  private const string SynchronizationContextType =
    "Autodesk.AutoCAD.Runtime.SynchronizationContext";
  private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(30);
  private static int _activeSubscriptions;
  [ThreadStatic] private static bool _handling;

  private readonly OperationProgress _progress;
  private readonly EventHandler<FirstChanceExceptionEventArgs> _handler;
  private readonly Timer _expiry;
  private int _disposed;
  private int _subscriptionOwned;

  internal static TimeSpan? LifetimeOverrideForTests { get; set; }
  internal static int ActiveSubscriptionsForTests => Volatile.Read(ref _activeSubscriptions);

  private FirstChanceCompletionObserver(OperationProgress progress)
  {
    _progress = progress;
    _handler = OnFirstChanceException;
    _expiry = new Timer(static state => ((FirstChanceCompletionObserver)state!).Stop(3), this,
      Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    try
    {
      Volatile.Write(ref _subscriptionOwned, 1);
      Interlocked.Increment(ref _activeSubscriptions);
      AppDomain.CurrentDomain.FirstChanceException += _handler;
      if (Volatile.Read(ref _disposed) == 0)
      {
        _progress.RecordFirstChanceObserverStarted();
        if (Volatile.Read(ref _disposed) == 0)
          _expiry.Change(LifetimeOverrideForTests ?? DefaultLifetime, Timeout.InfiniteTimeSpan);
      }
    }
    catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
    {
      // A matching event won after subscription and disposed the disabled timer.
    }
    catch
    {
      Stop(5);
      throw;
    }
  }

  internal static FirstChanceCompletionObserver? Start(OperationProgress? progress)
  {
    if (progress?.CompletionDiagnosticsEnabled != true) return null;
    try
    {
      return new FirstChanceCompletionObserver(progress);
    }
    catch
    {
      // Diagnostics must not prevent Civil's native scheduling call.
      return null;
    }
  }

  private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs eventArgs)
  {
    if (Volatile.Read(ref _disposed) != 0 || _handling) return;
    _handling = true;
    try
    {
      var hasContinuation = false;
      var hasPost = false;
      var frames = (new StackTrace(eventArgs.Exception, false).GetFrames() ?? [])
        .Concat(new StackTrace(false).GetFrames() ?? [])
        .Take(16);
      foreach (var frame in frames)
      {
        var method = frame.GetMethod();
        var typeName = method?.DeclaringType?.FullName;
        if (typeName == ExecutionResultType && method?.Name == "Continuation") hasContinuation = true;
        if (typeName == SynchronizationContextType && method?.Name == "Post") hasPost = true;
      }
      if (!hasContinuation && !hasPost ||
        Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
      try
      {
        _progress.RecordMatchingFirstChanceException(
          eventArgs.Exception switch
          {
            InvalidOperationException => 1,
            NullReferenceException => 2,
            TaskSchedulerException => 3,
            _ => 4,
          },
          System.Threading.SynchronizationContext.Current is null ? 1
            : System.Threading.SynchronizationContext.Current.GetType().FullName == SynchronizationContextType ? 2 : 3,
          ReferenceEquals(System.Threading.Tasks.TaskScheduler.Current, System.Threading.Tasks.TaskScheduler.Default)
            ? 1 : 2,
          hasContinuation,
          hasPost
        );
      }
      finally
      {
        // The first matching event is the whole observation budget.
        Cleanup();
        _progress.RecordFirstChanceObserverTerminalState(2);
      }
    }
    catch
    {
      // A first-chance observer must never change propagation of the original exception.
    }
    finally
    {
      _handling = false;
    }
  }

  public void Dispose()
  {
    Stop(4);
  }

  private void Stop(int state)
  {
    if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
    try
    {
      Cleanup();
    }
    finally
    {
      _progress.RecordFirstChanceObserverTerminalState(state);
    }
  }

  private void Cleanup()
  {
    if (Interlocked.Exchange(ref _subscriptionOwned, 0) != 0)
    {
      try { AppDomain.CurrentDomain.FirstChanceException -= _handler; }
      catch { }
      Interlocked.Decrement(ref _activeSubscriptions);
    }
    try { _expiry.Dispose(); }
    catch { }
  }
}
