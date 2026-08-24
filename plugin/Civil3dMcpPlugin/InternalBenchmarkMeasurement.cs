using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Civil3DMcpPlugin;

/// <summary>
/// Opt-in, request-scoped measurements used only by the phase 2A benchmark runner.
/// The object deliberately contains no code, result, error, drawing, or path data.
/// </summary>
internal sealed class InternalBenchmarkMeasurement
{
  public const string SchemaVersion = "civil3d-mcp-internal-measurement/v1";
  public const string ParameterName = "_benchmarkMeasurement";
  public const string ResponsePropertyName = "_benchmarkMeasurement";

  private static readonly Regex OpaqueRunIdentifier = new(
    "^run-[a-f0-9]{32}$",
    RegexOptions.CultureInvariant
  );

  private InternalBenchmarkMeasurement(string correlationId)
  {
    CorrelationId = correlationId;
  }

  public string CorrelationId { get; }
  public string? CodeSha256 { get; private set; }
  public int CacheHits { get; private set; }
  public int CacheMisses { get; private set; }
  public int CompilationAttempts { get; private set; }
  public int CompilationErrors { get; private set; }
  public double? CommandContextWaitMs { get; private set; }
  public double? ExecutionMs { get; private set; }

  public static InternalBenchmarkMeasurement? TryCreate(JsonObject? parameters)
  {
    if (parameters?[ParameterName] is not JsonObject request)
    {
      return null;
    }

    var schemaVersion = GetString(request, "schemaVersion");
    var correlationId = GetString(request, "correlationId");

    if (
      schemaVersion != SchemaVersion ||
      correlationId == null ||
      !OpaqueRunIdentifier.IsMatch(correlationId)
    )
    {
      return null;
    }

    return new InternalBenchmarkMeasurement(correlationId);
  }

  public void RecordCode(string code)
  {
    CodeSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)))
      .ToLowerInvariant();
  }

  public void RecordCacheLookup(bool hit)
  {
    if (hit)
    {
      CacheHits++;
    }
    else
    {
      CacheMisses++;
    }
  }

  public void RecordCompilationAttempt(bool failed)
  {
    CompilationAttempts++;
    if (failed)
    {
      CompilationErrors++;
    }
  }

  public void RecordCommandContextWait(TimeSpan elapsed)
  {
    CommandContextWaitMs = (CommandContextWaitMs ?? 0) + elapsed.TotalMilliseconds;
  }

  public void RecordExecution(TimeSpan elapsed)
  {
    ExecutionMs = elapsed.TotalMilliseconds;
  }

  public JsonObject ToJsonObject()
  {
    return new JsonObject
    {
      ["schemaVersion"] = SchemaVersion,
      ["correlationId"] = CorrelationId,
      ["codeSha256"] = CodeSha256,
      ["cacheHits"] = CacheHits,
      ["cacheMisses"] = CacheMisses,
      ["compilationAttempts"] = CompilationAttempts,
      ["compilationErrors"] = CompilationErrors,
      ["commandContextWaitMs"] = CommandContextWaitMs is double commandContextWaitMs
        ? JsonValue.Create(commandContextWaitMs)
        : null,
      ["executionMs"] = ExecutionMs is double executionMs ? JsonValue.Create(executionMs) : null,
    };
  }

  private static string? GetString(JsonObject value, string name)
  {
    return value[name] is JsonValue node && node.TryGetValue<string>(out var result)
      ? result
      : null;
  }
}
