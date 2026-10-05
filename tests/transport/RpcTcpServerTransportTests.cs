using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

internal static class RpcTcpServerTransportTests
{
  private static readonly UTF8Encoding StrictUtf8 = new(false, true);

  public static async Task Main()
  {
    var tests = new (string Name, Func<Task> Run)[]
    {
      ("fragmented UTF-8 request and LF response", FragmentedUtf8RequestAsync),
      ("oversized multibyte request never reaches handler", OversizedRequestAsync),
      ("connection close before LF never reaches handler", MissingDelimiterAsync),
      ("exact response limit is framed", ExactResponseLimitAsync),
      ("oversized post-execution response becomes bounded error", OversizedResponseAsync),
      ("client closing before the response cancels the request", PeerCloseCancelsRequestAsync),
      ("watched connection still answers a completed request", WatchedConnectionAnswersAsync),
    };

    foreach (var (name, run) in tests)
    {
      await run();
      Console.WriteLine($"PASS {name}");
    }
  }

  private static async Task FragmentedUtf8RequestAsync()
  {
    const string request =
      "{\"jsonrpc\":\"2.0\",\"method\":\"executeCode\",\"params\":{\"code\":\"árvíztűrő 🚲\"},\"id\":\"utf8-id\"}";
    var framedRequest = StrictUtf8.GetBytes(request + "\n");
    var emoji = StrictUtf8.GetBytes("🚲");
    var emojiIndex = framedRequest.AsSpan().IndexOf(emoji);
    Assert(emojiIndex >= 0, "emoji bytes must be present");

    await using var stream = new ScriptedDuplexStream(
      framedRequest[..(emojiIndex + 1)],
      framedRequest[(emojiIndex + 1)..(emojiIndex + 3)],
      framedRequest[(emojiIndex + 3)..]
    );
    var handlerCalls = 0;

    await RpcTcpServer.ProcessStreamAsync(
      stream,
      (actualRequest, _) =>
      {
        handlerCalls++;
        Assert(actualRequest == request, "fragmented UTF-8 request must round-trip exactly");
        return Task.FromResult("{\"jsonrpc\":\"2.0\",\"id\":\"utf8-id\",\"result\":\"ok\"}");
      },
      CancellationToken.None
    );

    Assert(handlerCalls == 1, "handler must run exactly once");
    var output = stream.WrittenBytes;
    Assert(output[^1] == (byte)'\n', "response must end in LF");
    var response = JsonNode.Parse(output[..^1]) as JsonObject;
    Assert(response?["result"]?.GetValue<string>() == "ok", "response body must be preserved");
  }

  private static async Task OversizedRequestAsync()
  {
    var characterCount = RpcTcpServer.MaxJsonBodyBytes / 2 + 1;
    var body = StrictUtf8.GetBytes(new string('é', characterCount));
    Assert(body.Length > RpcTcpServer.MaxJsonBodyBytes, "fixture must exceed the byte limit");
    Assert(characterCount < RpcTcpServer.MaxJsonBodyBytes, "fixture must prove byte rather than character counting");
    var frame = new byte[body.Length + 1];
    body.CopyTo(frame, 0);
    frame[^1] = (byte)'\n';

    await using var stream = new ScriptedDuplexStream(frame);
    var handlerCalls = 0;

    await RpcTcpServer.ProcessStreamAsync(
      stream,
      (_, _) =>
      {
        handlerCalls++;
        return Task.FromResult("{}");
      },
      CancellationToken.None
    );

    Assert(handlerCalls == 0, "oversized request must not reach the handler");
    AssertErrorFrame(stream.WrittenBytes, "CIVIL3D.REQUEST_TOO_LARGE", expectedId: null);
  }

  private static async Task MissingDelimiterAsync()
  {
    await using var stream = new ScriptedDuplexStream(StrictUtf8.GetBytes("{\"jsonrpc\":\"2.0\"}"));
    var handlerCalls = 0;

    await RpcTcpServer.ProcessStreamAsync(
      stream,
      (_, _) =>
      {
        handlerCalls++;
        return Task.FromResult("{}");
      },
      CancellationToken.None
    );

    Assert(handlerCalls == 0, "unterminated request must not reach the handler");
    AssertErrorFrame(stream.WrittenBytes, "CIVIL3D.TRANSPORT_ERROR", expectedId: null);
  }

  private static async Task ExactResponseLimitAsync()
  {
    const string request = "{\"jsonrpc\":\"2.0\",\"method\":\"executeCode\",\"id\":\"exact-id\"}";
    var response = CreateSizedResponse("exact-id", RpcTcpServer.MaxJsonBodyBytes);
    await using var stream = new ScriptedDuplexStream(StrictUtf8.GetBytes(request + "\n"));

    await RpcTcpServer.ProcessStreamAsync(
      stream,
      (_, _) => Task.FromResult(response),
      CancellationToken.None
    );

    var output = stream.WrittenBytes;
    Assert(output.Length == RpcTcpServer.MaxJsonBodyBytes + 1, "LF is outside the JSON body limit");
    Assert(output[^1] == (byte)'\n', "exact-limit response must end in LF");
    Assert(StrictUtf8.GetString(output[..^1]) == response, "exact-limit response must be unchanged");
  }

  private static async Task OversizedResponseAsync()
  {
    const string request = "{\"jsonrpc\":\"2.0\",\"method\":\"executeCode\",\"id\":\"large-id\"}";
    var response = CreateSizedResponse("large-id", RpcTcpServer.MaxJsonBodyBytes + 1);
    await using var stream = new ScriptedDuplexStream(StrictUtf8.GetBytes(request + "\n"));
    var handlerCalls = 0;

    await RpcTcpServer.ProcessStreamAsync(
      stream,
      (_, _) =>
      {
        handlerCalls++;
        return Task.FromResult(response);
      },
      CancellationToken.None
    );

    Assert(handlerCalls == 1, "oversized response fixture must represent post-execution failure");
    Assert(stream.WrittenBytes.Length - 1 <= RpcTcpServer.MaxJsonBodyBytes, "replacement error must be bounded");
    AssertErrorFrame(stream.WrittenBytes, "CIVIL3D.RESPONSE_TOO_LARGE", "large-id");
  }

  private static string CreateSizedResponse(string id, int targetBytes)
  {
    var prefix = $"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":\"";
    const string suffix = "\"}";
    var fillerBytes = targetBytes - StrictUtf8.GetByteCount(prefix) - StrictUtf8.GetByteCount(suffix);
    Assert(fillerBytes >= 0, "target response size is too small");
    var response = prefix + new string('x', fillerBytes) + suffix;
    Assert(StrictUtf8.GetByteCount(response) == targetBytes, "response fixture must have exact byte size");
    return response;
  }

  private static void AssertErrorFrame(byte[] frame, string expectedCode, string? expectedId)
  {
    Assert(frame.Length > 1 && frame[^1] == (byte)'\n', "error response must end in LF");
    var response = JsonNode.Parse(frame[..^1]) as JsonObject;
    Assert(response != null, "error response must be a JSON object");
    Assert(response!["error"]?["code"]?.GetValue<string>() == expectedCode, "unexpected error code");
    Assert(response["id"]?.GetValue<string?>() == expectedId, "request id must be preserved when known");
  }

  private static async Task PeerCloseCancelsRequestAsync()
  {
    var (server, client) = await ConnectLoopbackAsync();
    using (server)
    using (client)
    {
      var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var handlerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var processing = RpcTcpServer.ProcessStreamAsync(
        server.GetStream(),
        async (_, cancellationToken) =>
        {
          handlerStarted.SetResult();
          try
          {
            await Task.Delay(Timeout.Infinite, cancellationToken);
          }
          catch (OperationCanceledException)
          {
            handlerCancelled.SetResult();
            throw;
          }
          return "{}";
        },
        CancellationToken.None,
        watchForPeerClose: true
      );

      await client.GetStream().WriteAsync(StrictUtf8.GetBytes("{\"id\":\"gone\"}\n"));
      await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
      client.Close();

      await handlerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
      await processing.WaitAsync(TimeSpan.FromSeconds(5));
    }
  }

  private static async Task WatchedConnectionAnswersAsync()
  {
    var (server, client) = await ConnectLoopbackAsync();
    using (server)
    using (client)
    {
      var tokenWasCancelled = false;
      var processing = RpcTcpServer.ProcessStreamAsync(
        server.GetStream(),
        async (_, cancellationToken) =>
        {
          await Task.Delay(50);
          tokenWasCancelled = cancellationToken.IsCancellationRequested;
          return "{\"jsonrpc\":\"2.0\",\"id\":\"kept\",\"result\":\"ok\"}";
        },
        CancellationToken.None,
        watchForPeerClose: true
      );

      var clientStream = client.GetStream();
      await clientStream.WriteAsync(StrictUtf8.GetBytes("{\"id\":\"kept\"}\n"));
      var received = new MemoryStream();
      var buffer = new byte[256];
      while (received.Length == 0 || received.GetBuffer()[received.Length - 1] != (byte)'\n')
      {
        var read = await clientStream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(read > 0, "the response must arrive before the connection closes");
        received.Write(buffer, 0, read);
      }
      await processing.WaitAsync(TimeSpan.FromSeconds(5));

      Assert(!tokenWasCancelled, "an open connection must not cancel its request");
      var response = JsonNode.Parse(received.ToArray()[..^1]) as JsonObject;
      Assert(response?["result"]?.GetValue<string>() == "ok", "the response must be delivered");
    }
  }

  private static async Task<(TcpClient Server, TcpClient Client)> ConnectLoopbackAsync()
  {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
      var client = new TcpClient();
      var accepting = listener.AcceptTcpClientAsync();
      await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
      return (await accepting, client);
    }
    finally
    {
      listener.Stop();
    }
  }

  private static void Assert(bool condition, string message)
  {
    if (!condition) throw new InvalidOperationException(message);
  }

  private sealed class ScriptedDuplexStream : Stream
  {
    private readonly Queue<byte[]> _readChunks;
    private readonly MemoryStream _writes = new();
    private int _chunkOffset;

    public ScriptedDuplexStream(params byte[][] readChunks)
    {
      _readChunks = new Queue<byte[]>(readChunks);
    }

    public byte[] WrittenBytes => _writes.ToArray();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
      => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(
      Memory<byte> buffer,
      CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (_readChunks.Count == 0) return ValueTask.FromResult(0);

      var chunk = _readChunks.Peek();
      var count = Math.Min(buffer.Length, chunk.Length - _chunkOffset);
      chunk.AsMemory(_chunkOffset, count).CopyTo(buffer);
      _chunkOffset += count;
      if (_chunkOffset == chunk.Length)
      {
        _readChunks.Dequeue();
        _chunkOffset = 0;
      }
      return ValueTask.FromResult(count);
    }

    public override void Write(byte[] buffer, int offset, int count)
      => _writes.Write(buffer, offset, count);

    public override ValueTask WriteAsync(
      ReadOnlyMemory<byte> buffer,
      CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();
      _writes.Write(buffer.Span);
      return ValueTask.CompletedTask;
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken)
      => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
      if (disposing) _writes.Dispose();
      base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
      await _writes.DisposeAsync();
      GC.SuppressFinalize(this);
    }
  }
}
