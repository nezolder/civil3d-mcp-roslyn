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
  double StageElapsedMs
);

/// <summary>
/// An operation-scoped, Civil-API-free snapshot for diagnosing a pending call.
/// Recording progress never cancels work or releases the serialized gate.
/// </summary>
internal sealed class OperationProgress
{
  private readonly object _sync = new();
  private readonly long _startedAt = Stopwatch.GetTimestamp();
  private long _stageStartedAt;
  private OperationStage _stage = OperationStage.Dispatching;

  public OperationProgress() => _stageStartedAt = _startedAt;

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
      return new OperationProgressSnapshot(
        _stage.ToString(),
        Stopwatch.GetElapsedTime(_startedAt, now).TotalMilliseconds,
        Stopwatch.GetElapsedTime(_stageStartedAt, now).TotalMilliseconds
      );
    }
  }
}
