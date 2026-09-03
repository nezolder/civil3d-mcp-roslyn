using System.Diagnostics;

namespace Civil3DMcpPlugin;

internal sealed record SerializedOperationStatus(
  int WaitingCount,
  bool IsActive,
  string? CurrentOperation,
  OperationProgressSnapshot? Progress
);

/// <summary>
/// Limits Civil API dispatch to one active operation. SemaphoreSlim does not
/// provide a strict FIFO guarantee, so callers must rely only on maxConcurrency=1.
/// </summary>
internal sealed class SerializedOperationGate
{
  private readonly SemaphoreSlim _semaphore = new(1, 1);
  private readonly object _sync = new();
  private int _waitingCount;
  private bool _isActive;
  private string? _currentOperation;
  private OperationProgress? _progress;

  public SerializedOperationStatus GetStatus()
  {
    lock (_sync)
    {
      return new SerializedOperationStatus(
        _waitingCount,
        _isActive,
        _currentOperation,
        _progress?.GetStatus()
      );
    }
  }

  public async Task<Lease> EnterAsync(
    string operation,
    CancellationToken cancellationToken,
    InternalBenchmarkMeasurement? benchmarkMeasurement = null)
  {
    var waitStartedAt = benchmarkMeasurement == null
      ? (long?)null
      : Stopwatch.GetTimestamp();

    lock (_sync) { _waitingCount++; }

    try
    {
      await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    catch
    {
      lock (_sync) { _waitingCount--; }
      throw;
    }

    var activated = false;
    try
    {
      var progress = new OperationProgress();
      lock (_sync)
      {
        _waitingCount--;
        _isActive = true;
        _currentOperation = operation;
        _progress = progress;
        activated = true;
      }

      if (waitStartedAt is long startedAt)
      {
        benchmarkMeasurement!.RecordCommandContextWait(
          Stopwatch.GetElapsedTime(startedAt)
        );
      }

      return new Lease(this, progress);
    }
    catch
    {
      if (activated)
      {
        lock (_sync)
        {
          _isActive = false;
          _currentOperation = null;
          _progress = null;
        }
      }
      _semaphore.Release();
      throw;
    }
  }

  private void Exit()
  {
    lock (_sync)
    {
      _isActive = false;
      _currentOperation = null;
      _progress = null;
    }
    _semaphore.Release();
  }

  internal sealed class Lease : IDisposable
  {
    private SerializedOperationGate? _owner;

    public Lease(SerializedOperationGate owner, OperationProgress progress)
    {
      _owner = owner;
      Progress = progress;
    }

    public OperationProgress Progress { get; }

    public void Dispose()
    {
      Interlocked.Exchange(ref _owner, null)?.Exit();
    }
  }
}
