namespace Civil3DMcpPlugin;

public sealed class RpcTcpServer
{
  public static bool ThrowOnStart { get; set; }

  public RpcTcpServer(
    int port,
    Func<string, CancellationToken, Task<string>> handler)
  {
  }

  public void Start()
  {
    if (ThrowOnStart) throw new InvalidOperationException("expected listener start failure");
  }

  public void Stop() { }
}

public static class RoslynExecutor
{
  private static int _activeCount;
  private static int _callCount;
  private static int _maxActiveCount;

  public static Func<string, ScriptContext, Task<object?>> Handler { get; set; } =
    (_, _) => Task.FromResult<object?>("ok");

  public static int ActiveCount => Volatile.Read(ref _activeCount);
  public static int CallCount => Volatile.Read(ref _callCount);
  public static int MaxActiveCount => Volatile.Read(ref _maxActiveCount);

  internal static async Task<object?> ExecuteAsync(
    string code,
    ScriptContext context,
    InternalBenchmarkMeasurement? benchmarkMeasurement)
  {
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

  public static void Reset()
  {
    Volatile.Write(ref _activeCount, 0);
    Volatile.Write(ref _callCount, 0);
    Volatile.Write(ref _maxActiveCount, 0);
    Handler = (_, _) => Task.FromResult<object?>("ok");
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
