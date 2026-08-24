using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// TCP server that accepts JSON-RPC requests from the MCP Node.js server.
/// Each connection carries one LF-delimited UTF-8 JSON request and response.
/// </summary>
public sealed class RpcTcpServer
{
  internal const int MaxJsonBodyBytes = 8 * 1024 * 1024;

  private const byte FrameDelimiter = (byte)'\n';
  private static readonly UTF8Encoding StrictUtf8 = new(false, true);
  private static readonly byte[] FrameDelimiterBytes = [FrameDelimiter];
  private readonly int _port;
  private readonly Func<string, CancellationToken, Task<string>> _handler;
  private readonly CancellationTokenSource _cts = new();
  private TcpListener? _listener;
  private Task? _acceptLoop;

  public RpcTcpServer(int port, Func<string, CancellationToken, Task<string>> handler)
  {
    _port = port;
    _handler = handler;
  }

  public void Start()
  {
    _listener = new TcpListener(IPAddress.Loopback, _port);
    _listener.Start();
    _acceptLoop = Task.Run(AcceptLoopAsync, _cts.Token);
  }

  public void Stop()
  {
    try
    {
      _cts.Cancel();
      _listener?.Stop();
      _acceptLoop?.Wait(TimeSpan.FromSeconds(1));
    }
    catch
    {
      // Swallow exceptions during shutdown
    }
  }

  private async Task AcceptLoopAsync()
  {
    while (!_cts.IsCancellationRequested)
    {
      TcpClient? client = null;
      try
      {
        client = await _listener!.AcceptTcpClientAsync(_cts.Token);
        _ = Task.Run(() => ProcessClientAsync(client, _cts.Token), _cts.Token);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch
      {
        client?.Dispose();
      }
    }
  }

  private async Task ProcessClientAsync(TcpClient client, CancellationToken cancellationToken)
  {
    using (client)
    await using (var stream = client.GetStream())
    {
      try
      {
        await ProcessStreamAsync(stream, _handler, cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        // Normal shutdown.
      }
      catch (IOException)
      {
        // The peer disconnected while this one-shot connection was completing.
      }
    }
  }

  internal static async Task ProcessStreamAsync(
    Stream stream,
    Func<string, CancellationToken, Task<string>> handler,
    CancellationToken cancellationToken)
  {
    string request;
    try
    {
      request = await ReadFramedJsonBodyAsync(stream, cancellationToken);
    }
    catch (RpcTransportException ex)
    {
      await WriteFramedResponseAsync(
        stream,
        CreateTransportErrorResponse(null, ex.Code, ex.Message),
        cancellationToken
      );
      return;
    }

    var response = await handler(request, cancellationToken);
    var boundedResponse = CreateBoundedResponse(request, response);
    await WriteFramedResponseAsync(stream, boundedResponse, cancellationToken);
  }

  internal static async Task<string> ReadFramedJsonBodyAsync(
    Stream stream,
    CancellationToken cancellationToken)
  {
    var readBuffer = new byte[8192];
    using var body = new MemoryStream();

    while (!cancellationToken.IsCancellationRequested)
    {
      var bytesRead = await stream.ReadAsync(readBuffer, cancellationToken);
      if (bytesRead <= 0)
      {
        throw new RpcTransportException(
          "CIVIL3D.TRANSPORT_ERROR",
          "Connection closed before the LF request frame delimiter was received."
        );
      }

      var delimiterIndex = Array.IndexOf(readBuffer, FrameDelimiter, 0, bytesRead);
      var bodyBytesInChunk = delimiterIndex < 0 ? bytesRead : delimiterIndex;

      if (body.Length + bodyBytesInChunk > MaxJsonBodyBytes)
      {
        throw new RpcTransportException(
          "CIVIL3D.REQUEST_TOO_LARGE",
          $"Request exceeded the {MaxJsonBodyBytes}-byte UTF-8 JSON body limit."
        );
      }

      if (bodyBytesInChunk > 0)
      {
        body.Write(readBuffer, 0, bodyBytesInChunk);
      }

      if (delimiterIndex < 0) continue;

      if (delimiterIndex != bytesRead - 1)
      {
        throw new RpcTransportException(
          "CIVIL3D.TRANSPORT_ERROR",
          "Data followed the LF request frame delimiter."
        );
      }

      try
      {
        return StrictUtf8.GetString(body.GetBuffer(), 0, checked((int)body.Length));
      }
      catch (DecoderFallbackException)
      {
        throw new RpcTransportException(
          "CIVIL3D.TRANSPORT_ERROR",
          "Request body is not valid UTF-8."
        );
      }
    }

    throw new OperationCanceledException(cancellationToken);
  }

  internal static string CreateBoundedResponse(string request, string response)
  {
    try
    {
      if (StrictUtf8.GetByteCount(response) <= MaxJsonBodyBytes)
      {
        return response;
      }

      return CreateTransportErrorResponse(
        TryGetRequestId(request),
        "CIVIL3D.RESPONSE_TOO_LARGE",
        $"Response exceeded the {MaxJsonBodyBytes}-byte UTF-8 JSON body limit after execution; execution outcome is unknown."
      );
    }
    catch (EncoderFallbackException)
    {
      return CreateTransportErrorResponse(
        TryGetRequestId(request),
        "CIVIL3D.TRANSPORT_ERROR",
        "Response was not valid UTF-8 after execution; execution outcome is unknown."
      );
    }
  }

  internal static async Task WriteFramedResponseAsync(
    Stream stream,
    string response,
    CancellationToken cancellationToken)
  {
    var responseBytes = StrictUtf8.GetBytes(response);
    await stream.WriteAsync(responseBytes, cancellationToken);
    await stream.WriteAsync(FrameDelimiterBytes, cancellationToken);
    await stream.FlushAsync(cancellationToken);
  }

  private static JsonNode? TryGetRequestId(string request)
  {
    try
    {
      return (JsonNode.Parse(request) as JsonObject)?["id"]?.DeepClone();
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private static string CreateTransportErrorResponse(
    JsonNode? id,
    string code,
    string message)
  {
    var response = new JsonObject
    {
      ["jsonrpc"] = "2.0",
      ["id"] = id,
      ["error"] = new JsonObject
      {
        ["code"] = code,
        ["message"] = message,
      },
    };
    var serialized = response.ToJsonString();

    if (StrictUtf8.GetByteCount(serialized) <= MaxJsonBodyBytes)
    {
      return serialized;
    }

    response["id"] = null;
    return response.ToJsonString();
  }
}

internal sealed class RpcTransportException : Exception
{
  public RpcTransportException(string code, string message) : base(message)
  {
    Code = code;
  }

  public string Code { get; }
}
