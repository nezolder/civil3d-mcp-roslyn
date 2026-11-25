using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Sockets;

namespace Civil3DMcpPlugin;

/// <summary>Status record for plugin diagnostics.</summary>
public sealed record PluginStatus(
  bool IsRunning,
  bool OperationInProgress,
  string? CurrentOperation,
  int QueueDepth,
  string InstanceId,
  int ProcessId,
  int Port,
  DateTimeOffset StartedAtUtc,
  string? OperationStage = null,
  double? OperationElapsedMs = null,
  double? StageElapsedMs = null,
  CommandContextDiagnosticSnapshot? CompletionDiagnostics = null,
  CommandContextDiagnosticSnapshot? LastCompletionDiagnostics = null
);

/// <summary>
/// Exception type for JSON-RPC dispatch errors, carrying an error code.
/// </summary>
public sealed class JsonRpcDispatchException : Exception
{
  public JsonRpcDispatchException(
    string code,
    string message,
    bool operationCommitted = false) : base(message)
  {
    Code = code;
    OperationCommitted = operationCommitted;
  }

  public string Code { get; }
  public bool OperationCommitted { get; }
}

/// <summary>
/// Manages the plugin lifecycle: starts/stops the TCP server,
/// handles raw JSON-RPC requests, and provides parameter extraction helpers.
/// </summary>
public static class PluginRuntime
{
  public const int DefaultPort = 8080;

  public static string InstanceId { get; } = Guid.NewGuid().ToString("N");
  public static DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
  public static int Port { get; private set; } = DefaultPort;

  private static readonly object Sync = new();
  private static readonly SerializedOperationGate OperationGate = new();
  private static readonly IdempotencyRegistry Idempotency = new();
  private static RpcTcpServer? _server;
  private static EndpointRegistration? _endpointRegistration;
  private static string? _sessionToken;

  /// <summary>Whether requests over the listener must present the session token.</summary>
  public static bool TokenRequired => SessionAuth.IsRequired;

  public static void StartServer()
  {
    lock (Sync)
    {
      if (_server != null) return;
      RpcTcpServer? server = null;
      EndpointRegistration? registration = null;
      try
      {
        // A new token for every listener start; it lives as long as the record.
        var token = SessionAuth.CreateToken();
        Volatile.Write(ref _sessionToken, token);
        server = new RpcTcpServer(DefaultPort, HandleTransportRequestAsync);
        try
        {
          server.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
          server.Stop();
          server = new RpcTcpServer(0, HandleTransportRequestAsync);
          server.Start();
        }

        var boundPort = server.BoundPort;
        registration = EndpointRegistration.Create(InstanceId, boundPort, StartedAtUtc, token);
        Port = boundPort;
        _server = server;
        _endpointRegistration = registration;
      }
      catch
      {
        registration?.Dispose();
        server?.Stop();
        Port = DefaultPort;
        Volatile.Write(ref _sessionToken, null);
        throw;
      }
    }
  }

  public static void StopServer()
  {
    lock (Sync)
    {
      _server?.Stop();
      _endpointRegistration?.Dispose();
      _server = null;
      _endpointRegistration = null;
      Port = DefaultPort;
      Volatile.Write(ref _sessionToken, null);
    }
  }

  public static PluginStatus GetStatus()
  {
    bool isRunning;
    lock (Sync)
    {
      isRunning = _server != null;
    }

    var operationStatus = OperationGate.GetStatus();
    return new PluginStatus(
      isRunning,
      operationStatus.IsActive,
      operationStatus.CurrentOperation,
      operationStatus.WaitingCount,
      InstanceId,
      Environment.ProcessId,
      Port,
      StartedAtUtc,
      operationStatus.Progress?.Stage,
      operationStatus.Progress?.OperationElapsedMs,
      operationStatus.Progress?.StageElapsedMs,
      operationStatus.Progress?.CompletionDiagnostics,
      operationStatus.LastCompletionDiagnostics
    );
  }

  /// <summary>
  /// Parses a raw JSON-RPC request string, dispatches to the appropriate
  /// command handler, and returns the serialized JSON-RPC response. This is
  /// the in-process entry; it asks for no token.
  /// </summary>
  public static Task<string> HandleRawRequestAsync(
    string rawRequest,
    CancellationToken cancellationToken)
    => HandleRequestAsync(rawRequest, requireToken: false, cancellationToken);

  /// <summary>
  /// What the TCP listener calls: the same handling, but a request must
  /// present this session's token (see <see cref="SessionAuth"/>).
  /// </summary>
  internal static Task<string> HandleTransportRequestAsync(
    string rawRequest,
    CancellationToken cancellationToken)
    => HandleRequestAsync(rawRequest, SessionAuth.IsRequired, cancellationToken);

  private static async Task<string> HandleRequestAsync(
    string rawRequest,
    bool requireToken,
    CancellationToken cancellationToken)
  {
    JsonNode? parsed;
    try
    {
      parsed = JsonNode.Parse(rawRequest);
    }
    catch (Exception ex)
    {
      return SerializeError(null, "CIVIL3D.INVALID_INPUT", $"Invalid JSON request: {ex.Message}");
    }

    if (parsed is not JsonObject request)
    {
      return SerializeError(null, "CIVIL3D.INVALID_INPUT", "JSON-RPC request must be an object.");
    }

    var id = request["id"]?.DeepClone();
    var method = request["method"]?.GetValue<string>();
    var parameters = request["params"] as JsonObject;
    var benchmarkMeasurement = InternalBenchmarkMeasurement.TryCreate(parameters);

    if (string.IsNullOrWhiteSpace(method))
    {
      return SerializeError(id, "CIVIL3D.INVALID_INPUT", "JSON-RPC request is missing method.");
    }

    if (requireToken
      && !SessionAuth.Matches(Volatile.Read(ref _sessionToken), request[SessionAuth.RequestPropertyName]))
    {
      // Health still says that a plugin is here and which build it is, so the
      // caller can tell "wrong token" from "no Civil 3D". Nothing else runs,
      // and nothing about the drawing is told.
      return method == "getCivil3DHealth"
        ? SerializeResult(id, CommandDispatcher.GetLimitedHealth())
        : SerializeError(id, SessionAuth.RefusalCode, SessionAuth.RefusalMessage);
    }

    // Receipt and journal data of an executeCode request; unused for the rest.
    var record = method == "executeCode" ? ExecutionRecord.FromRequest(parameters) : null;
    var startedAt = Stopwatch.GetTimestamp();
    string? journalErrorCode = null;
    IdempotencyReservation reservation = IdempotencyReservation.None;
    try
    {
      object? result;
      if (method is "getCivil3DHealth" or "apiLookup")
      {
        result = await CommandDispatcher.DispatchAsync(
          method,
          parameters,
          cancellationToken,
          benchmarkMeasurement
        );
      }
      else
      {
        reservation = ReserveWriteIdempotency(method, parameters);
        using (var lease = await OperationGate.EnterAsync(method, cancellationToken, benchmarkMeasurement))
        {
          result = await CommandDispatcher.DispatchAsync(
            method,
            parameters,
            cancellationToken,
            benchmarkMeasurement,
            lease.Progress,
            record
          );
        }
        reservation.Complete();
      }
      return SerializeResult(id, result, benchmarkMeasurement, record);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      journalErrorCode = "CIVIL3D.CANCELLED";
      throw;
    }
    catch (JsonRpcDispatchException ex)
    {
      journalErrorCode = ex.Code;
      if (ex.OperationCommitted)
      {
        reservation.Complete();
      }
      return SerializeError(id, ex.Code, ex.Message, benchmarkMeasurement, record);
    }
    catch (Exception ex)
    {
      journalErrorCode = "CIVIL3D.TRANSACTION_FAILED";
      return SerializeError(
        id,
        "CIVIL3D.TRANSACTION_FAILED",
        ex.Message,
        benchmarkMeasurement,
        record
      );
    }
    finally
    {
      reservation.ReleaseIfIncomplete();
      if (record is { IsWrite: true })
      {
        WriteJournal.Append(
          record, journalErrorCode, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
      }
    }
  }

  internal static void ResetIdempotencyForTests() => Idempotency.ResetForTests();

  private static IdempotencyReservation ReserveWriteIdempotency(
    string method,
    JsonObject? parameters)
  {
    if (method != "executeCode") return IdempotencyReservation.None;

    var readOnly = GetOptionalBool(parameters, "readOnly") ?? false;
    if (parameters?.ContainsKey("idempotencyKey") != true) return IdempotencyReservation.None;
    if (!readOnly && (GetOptionalBool(parameters, "preview") ?? false))
    {
      // A preview commits nothing, so it must not use up the key that the
      // real write will carry. The key is still checked for its form.
      IdempotencyRegistry.ParseOptionalKey(parameters);
      return IdempotencyReservation.None;
    }
    if (readOnly)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "Parameter 'idempotencyKey' is only valid when readOnly is false."
      );
    }

    // Validate every part of the binding before acquiring the serialized Civil gate.
    var code = GetRequiredString(parameters, "code");
    var expectedDrawing = DrawingGuard.Parse(parameters, required: true)!;
    var saveDrawing = GetOptionalBool(parameters, "saveDrawing") ?? false;
    return Idempotency.Reserve(parameters, code, expectedDrawing, saveDrawing);
  }

  // ── Parameter extraction helpers ──

  public static string GetRequiredString(JsonObject? parameters, string name)
  {
    var value = parameters?[name];
    if (value == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    var s = value.GetValue<string>();
    if (string.IsNullOrWhiteSpace(s))
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' must be a non-empty string.");
    return s;
  }

  public static double GetRequiredDouble(JsonObject? parameters, string name)
  {
    var value = parameters?[name];
    if (value == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    return value.GetValue<double>();
  }

  public static int GetRequiredInt(JsonObject? parameters, string name)
  {
    var value = parameters?[name];
    if (value == null)
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    return value.GetValue<int>();
  }

  public static string? GetOptionalString(JsonObject? parameters, string name)
    => parameters?[name]?.GetValue<string?>();

  public static double? GetOptionalDouble(JsonObject? parameters, string name)
    => parameters?[name] != null ? parameters[name]!.GetValue<double>() : null;

  public static int? GetOptionalInt(JsonObject? parameters, string name)
    => parameters?[name] != null ? parameters[name]!.GetValue<int>() : null;

  public static bool? GetOptionalBool(JsonObject? parameters, string name)
  {
    var value = parameters?[name];
    if (value == null) return null;
    if (value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var parsed))
    {
      return parsed;
    }
    throw new JsonRpcDispatchException(
      "CIVIL3D.INVALID_INPUT",
      $"Parameter '{name}' must be a boolean."
    );
  }

  // ── JSON-RPC serialization ──

  private static string SerializeResult(
    JsonNode? id,
    object? result,
    InternalBenchmarkMeasurement? benchmarkMeasurement = null,
    ExecutionRecord? record = null)
  {
    var response = new JsonObject
    {
      ["jsonrpc"] = "2.0",
      ["id"] = id,
      ["result"] = result == null ? null : JsonSerializer.SerializeToNode(result, JsonSerializerOptions),
    };
    if (benchmarkMeasurement != null)
    {
      response[InternalBenchmarkMeasurement.ResponsePropertyName] =
        benchmarkMeasurement.ToJsonObject();
    }
    AddReceipt(response, record);
    AddApiNotes(response, record);
    AddImages(response, record);
    return response.ToJsonString(JsonSerializerOptions);
  }

  /// <summary>
  /// Image files the script returned travel next to the result, like the
  /// receipt; the result itself holds only a numbered reference to each.
  /// </summary>
  private static void AddImages(JsonObject response, ExecutionRecord? record)
  {
    if (record == null || record.Images.Count == 0) return;
    var images = new JsonArray();
    foreach (var image in record.Images)
    {
      images.Add(new JsonObject
      {
        ["mimeType"] = image.MimeType,
        ["data"] = Convert.ToBase64String(image.Data),
      });
    }
    response[ImagesPropertyName] = images;
  }

  internal const string ImagesPropertyName = "images";

  /// <summary>
  /// Notes on API members the script uses travel next to the result, on a
  /// success and on an error alike: a crash or a wrong value is often what
  /// the note is about.
  /// </summary>
  private static void AddApiNotes(JsonObject response, ExecutionRecord? record)
  {
    if (record == null || record.Notes.Count == 0) return;
    var notes = new JsonArray();
    foreach (var found in record.Notes)
    {
      notes.Add(new JsonObject
      {
        ["id"] = found.Id,
        ["member"] = found.Member,
        ["line"] = found.Line,
        ["source"] = found.Source,
        ["note"] = found.Text,
        ["guideTopic"] = found.GuideTopic,
      });
    }
    response[ApiNotesPropertyName] = notes;
  }

  internal const string ApiNotesPropertyName = "apiNotes";

  /// <summary>
  /// A write that reached the drawing reports what happened to it next to the
  /// result, so the result itself keeps its shape.
  /// </summary>
  private static void AddReceipt(JsonObject response, ExecutionRecord? record)
  {
    if (record is { IsWrite: true, ReachedDrawing: true })
    {
      response[ReceiptPropertyName] = record.ToReceipt();
    }
  }

  internal const string ReceiptPropertyName = "receipt";

  private static string SerializeError(
    JsonNode? id,
    string code,
    string message,
    InternalBenchmarkMeasurement? benchmarkMeasurement = null,
    ExecutionRecord? record = null)
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
    if (benchmarkMeasurement != null)
    {
      response[InternalBenchmarkMeasurement.ResponsePropertyName] =
        benchmarkMeasurement.ToJsonObject();
    }
    AddReceipt(response, record);
    AddApiNotes(response, record);
    return response.ToJsonString(JsonSerializerOptions);
  }

  private static readonly JsonSerializerOptions JsonSerializerOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
  };
}
