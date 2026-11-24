namespace Civil3DMcpPlugin;

public sealed class RpcTcpServer
{
  public static bool ThrowOnStart { get; set; }
  public static bool ThrowAddressInUseOnDefaultPort { get; set; }
  private readonly int _requestedPort;

  public RpcTcpServer(
    int port,
    Func<string, CancellationToken, Task<string>> handler)
  {
    _requestedPort = port;
  }

  public int BoundPort { get; private set; }

  public void Start()
  {
    if (ThrowOnStart) throw new InvalidOperationException("expected listener start failure");
    if (ThrowAddressInUseOnDefaultPort && _requestedPort == PluginRuntime.DefaultPort)
    {
      throw new System.Net.Sockets.SocketException(
        (int)System.Net.Sockets.SocketError.AddressAlreadyInUse
      );
    }
    BoundPort = _requestedPort == 0 ? 48123 : _requestedPort;
  }

  public void Stop() { }
}

/// <summary>
/// Stands in for the tracker that reads the drawing through the Autodesk API.
/// A test says what the "script" changed; the double records how it was used.
/// </summary>
internal sealed class DrawingChangeTracker : IDisposable
{
  /// <summary>Fills the list of the next tracked write; null means nothing changed.</summary>
  public static Action<ChangeList>? Changes { get; set; }
  public static bool FailToStart { get; set; }
  public static int StartCount { get; private set; }
  public static int DisposeCount { get; private set; }
  public static int? LastItemLimit { get; private set; }
  public static bool? LastPreview { get; private set; }
  public static bool? LastCommitted { get; private set; }
  /// <summary>Transactions that were open when the tracker was told the script had ended.</summary>
  public static int? ActiveTransactionsAtCapture { get; private set; }
  /// <summary>Transactions still open when the result was read back; must be zero.</summary>
  public static int? ActiveTransactionsAtFinish { get; private set; }

  private readonly Autodesk.AutoCAD.DatabaseServices.Database _database;
  private readonly int _itemLimit;

  private DrawingChangeTracker(Autodesk.AutoCAD.DatabaseServices.Database database, int itemLimit)
  {
    _database = database;
    _itemLimit = itemLimit;
  }

  internal static DrawingChangeTracker? Start(
    Autodesk.AutoCAD.DatabaseServices.Database database, int itemLimit)
  {
    StartCount++;
    LastItemLimit = itemLimit;
    return FailToStart ? null : new DrawingChangeTracker(database, itemLimit);
  }

  internal void BeforeTransactionEnds(
    Autodesk.AutoCAD.DatabaseServices.Transaction transaction, bool preview)
  {
    LastPreview = preview;
    ActiveTransactionsAtCapture = _database.TransactionManager.ActiveCount;
  }

  internal ChangeList? Finish(bool committed, bool preview)
  {
    LastCommitted = committed;
    ActiveTransactionsAtFinish = _database.TransactionManager.ActiveCount;
    if (!committed && !preview) return null;
    var list = new ChangeList(_itemLimit) { Preview = preview };
    Changes?.Invoke(list);
    if (preview) list.LeftAfterPreview ??= 0;
    return list;
  }

  public void Dispose() => DisposeCount++;

  public static void Reset()
  {
    Changes = null;
    FailToStart = false;
    StartCount = 0;
    DisposeCount = 0;
    LastItemLimit = null;
    LastPreview = null;
    LastCommitted = null;
    ActiveTransactionsAtCapture = null;
    ActiveTransactionsAtFinish = null;
  }
}

public static class RoslynExecutor
{
  private static int _activeCount;
  private static int _callCount;
  private static int _maxActiveCount;

  public const int MaxTimeoutSeconds = 1_800;

  public static Func<string, ScriptContext, Task<object?>> Handler { get; set; } =
    (_, _) => Task.FromResult<object?>("ok");

  /// <summary>Script limit the dispatcher passed on the most recent call.</summary>
  public static TimeSpan? LastTimeLimit { get; private set; }

  public static int ActiveCount => Volatile.Read(ref _activeCount);
  public static int CallCount => Volatile.Read(ref _callCount);
  public static int MaxActiveCount => Volatile.Read(ref _maxActiveCount);

  internal static async Task<object?> ExecuteAsync(
    string code,
    ScriptContext context,
    InternalBenchmarkMeasurement? benchmarkMeasurement,
    OperationProgress? progress = null,
    CancellationToken cancellationToken = default,
    TimeSpan? timeLimit = null)
  {
    LastTimeLimit = timeLimit;
    progress?.SetStage(OperationStage.RunningScript);
    Interlocked.Increment(ref _callCount);
    var active = Interlocked.Increment(ref _activeCount);
    SetMaximum(active);
    try
    {
      return await Handler(code, context);
    }
    finally
    {
      Interlocked.Decrement(ref _activeCount);
    }
  }

  internal static T RunWithScriptCancellation<T>(
    Func<T> action,
    CancellationToken cancellationToken,
    TimeSpan? timeLimit = null)
    => action();

  public static void Reset()
  {
    Volatile.Write(ref _activeCount, 0);
    Volatile.Write(ref _callCount, 0);
    Volatile.Write(ref _maxActiveCount, 0);
    Handler = (_, _) => Task.FromResult<object?>("ok");
    LastTimeLimit = null;
  }

  private static void SetMaximum(int candidate)
  {
    while (true)
    {
      var observed = Volatile.Read(ref _maxActiveCount);
      if (candidate <= observed) return;
      if (Interlocked.CompareExchange(ref _maxActiveCount, candidate, observed) == observed)
      {
        return;
      }
    }
  }
}
