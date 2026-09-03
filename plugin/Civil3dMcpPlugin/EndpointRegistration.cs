using System.Text;
using System.Text.Json;

namespace Civil3DMcpPlugin;

/// <summary>
/// Publishes one small, drawing-free endpoint record for the lifetime of the
/// current Civil 3D plugin session. The Node host probes records before use, so
/// files left behind by a crashed process are harmless.
/// </summary>
internal sealed class EndpointRegistration : IDisposable
{
  internal const int SchemaVersion = 1;
  internal const string DirectoryEnvironmentVariable = "CIVIL3D_MCP_ENDPOINT_DIR";

  private readonly string _filePath;
  private bool _disposed;

  private EndpointRegistration(string filePath)
  {
    _filePath = filePath;
  }

  internal string FilePath => _filePath;

  internal static EndpointRegistration Create(
    string instanceId,
    int port,
    DateTimeOffset startedAtUtc)
  {
    var directory = ResolveDirectory();
    Directory.CreateDirectory(directory);

    var processId = Environment.ProcessId;
    // Timestamp-first naming lets the bounded Node reader prefer recent live
    // sessions even if many records were left behind by earlier crashes.
    var fileName = $"{startedAtUtc.ToUnixTimeMilliseconds()}-{processId}-{instanceId}.json";
    var filePath = Path.Combine(directory, fileName);
    var temporaryPath = filePath + $".tmp-{Guid.NewGuid():N}";
    var payload = new EndpointRecord(
      SchemaVersion,
      instanceId,
      processId,
      port,
      startedAtUtc
    );
    var json = JsonSerializer.Serialize(payload, JsonOptions);

    try
    {
      File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
      File.Move(temporaryPath, filePath, true);
      return new EndpointRegistration(filePath);
    }
    catch
    {
      TryDelete(temporaryPath);
      throw;
    }
  }

  internal static string ResolveDirectory()
  {
    var configured = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
    if (!string.IsNullOrWhiteSpace(configured))
    {
      if (!Path.IsPathFullyQualified(configured))
      {
        throw new InvalidOperationException(
          $"{DirectoryEnvironmentVariable} must be an absolute path."
        );
      }
      return Path.GetFullPath(configured);
    }

    var localApplicationData = Environment.GetFolderPath(
      Environment.SpecialFolder.LocalApplicationData
    );
    return Path.Combine(localApplicationData, "Civil3dMcpRoslyn", "endpoints");
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    TryDelete(_filePath);
  }

  private static void TryDelete(string path)
  {
    try
    {
      if (File.Exists(path)) File.Delete(path);
    }
    catch
    {
      // A stale record is ignored by live probing on the Node side.
    }
  }

  private sealed record EndpointRecord(
    int SchemaVersion,
    string InstanceId,
    int ProcessId,
    int Port,
    DateTimeOffset StartedAtUtc
  );

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
  };
}
