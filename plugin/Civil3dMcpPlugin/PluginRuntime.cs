using System.Text.Json;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>Status record for plugin diagnostics.</summary>
public sealed record PluginStatus(
  bool IsRunning,
  bool OperationInProgress,
  string? CurrentOperation,
  int QueueDepth
);

/// <summary>
/// Exception type for JSON-RPC dispatch errors, carrying an error code.
/// </summary>
public sealed class JsonRpcDispatchException : Exception
{
  public JsonRpcDispatchException(string code, string message) : base(message)
  {
    Code = code;
  }

  public string Code { get; }
}

/// <summary>
/// Manages the plugin lifecycle: starts/stops the TCP server,
/// handles raw JSON-RPC requests, and provides parameter extraction helpers.
/// </summary>
public static class PluginRuntime
{
  public const int Port = 8080;

  private static readonly object Sync = new();
  private static readonly SerializedOperationGate OperationGate = new();
  private static readonly IdempotencyRegistry Idempotency = new();
  private static RpcTcpServer? _server;

  public static void StartServer()
  {
    lock (Sync)
    {
      if (_server != null) return;
      var server = new RpcTcpServer(Port, HandleRawRequestAsync);
      server.Start();
      _server = server;
    }
  }

  public static void StopServer()
  {
    lock (Sync)
    {
      _server?.Stop();
      _server = null;
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
      operationStatus.WaitingCount
    );
  }

  /// <summary>
  /// Parses a raw JSON-RPC request string, dispatches to the appropriate
  /// command handler, and returns the serialized JSON-RPC response.
  /// </summary>
  public static async Task<string> HandleRawRequestAsync(
    string rawRequest,
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
        using (await OperationGate.EnterAsync(method, cancellationToken, benchmarkMeasurement))
        {
          result = await CommandDispatcher.DispatchAsync(
            method,
            parameters,
            cancellationToken,
            benchmarkMeasurement
          );
        }
        reservation.Complete();
      }
      return SerializeResult(id, result, benchmarkMeasurement);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (JsonRpcDispatchException ex)
    {
      return SerializeError(id, ex.Code, ex.Message, benchmarkMeasurement);
    }
    catch (Exception ex)
    {
      return SerializeError(
        id,
        "CIVIL3D.TRANSACTION_FAILED",
        ex.Message,
        benchmarkMeasurement
      );
    }
    finally
    {
      reservation.ReleaseIfIncomplete();
    }
  }

  internal static void ResetIdempotencyForTests() => Idempotency.ResetForTests();

  private static IdempotencyReservation ReserveWriteIdempotency(
    string method,
    JsonObject? parameters)
  {
    if (method != "executeCode") return IdempotencyReservation.None;

    var readOnly = parameters?["readOnly"]?.GetValue<bool>() ?? false;
    if (parameters?.ContainsKey("idempotencyKey") != true) return IdempotencyReservation.None;
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
    return Idempotency.Reserve(parameters, code, expectedDrawing);
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
    => parameters?[name] != null ? parameters[name]!.GetValue<bool>() : null;

  // ── JSON-RPC serialization ──

  private static string SerializeResult(
    JsonNode? id,
    object? result,
    InternalBenchmarkMeasurement? benchmarkMeasurement = null)
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
    return response.ToJsonString(JsonSerializerOptions);
  }

  private static string SerializeError(
    JsonNode? id,
    string code,
    string message,
    InternalBenchmarkMeasurement? benchmarkMeasurement = null)
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
    return response.ToJsonString(JsonSerializerOptions);
  }

  private static readonly JsonSerializerOptions JsonSerializerOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
  };
}
