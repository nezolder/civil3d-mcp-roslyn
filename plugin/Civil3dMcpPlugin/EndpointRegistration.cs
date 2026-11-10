using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Civil3DMcpPlugin;

/// <summary>
/// Publishes one small, drawing-free endpoint record for the lifetime of the
/// current Civil 3D plugin session. The Node host probes records before use, so
/// files left behind by a crashed process are harmless; each new session also
/// removes the records of processes that no longer exist, so they do not pile
/// up and slow discovery down.
/// </summary>
internal sealed class EndpointRegistration : IDisposable
{
  internal const int SchemaVersion = 1;
  internal const string DirectoryEnvironmentVariable = "CIVIL3D_MCP_ENDPOINT_DIR";
  private const int MaxRecordBytes = 4_096;
  private const int MaxRecordsToInspect = 512;
  private static readonly TimeSpan ProcessStartTolerance = TimeSpan.FromSeconds(5);

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
    RemoveStaleRecords(directory);

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

  /// <summary>
  /// Deletes records whose process is gone: Civil 3D crashed or was killed
  /// before it could remove its own record. Best effort and conservative: a
  /// record is removed only when it is certain that its process does not run.
  /// </summary>
  internal static int RemoveStaleRecords(string directory)
  {
    var removed = 0;
    try
    {
      foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(MaxRecordsToInspect))
      {
        try
        {
          var file = new FileInfo(path);
          if (file.Length > MaxRecordBytes) continue;
          var record = JsonSerializer.Deserialize<EndpointRecord>(File.ReadAllText(path), JsonOptions);
          if (record is null || record.SchemaVersion != SchemaVersion || record.ProcessId <= 0) continue;
          if (record.ProcessId == Environment.ProcessId) continue;
          if (IsRecordedProcessRunning(record.ProcessId, record.StartedAtUtc)) continue;
          File.Delete(path);
          removed++;
        }
        catch (Exception)
        {
          // Unreadable, foreign or locked files are left alone.
        }
      }
    }
    catch (Exception)
    {
      // Cleaning up is optional; publishing this session's record is not.
    }
    return removed;
  }

  /// <summary>
  /// False only when no process has this id, or the process with this id
  /// started after the record was written (the id was reused). Anything
  /// uncertain, such as a process that may not be inspected, counts as running.
  /// </summary>
  internal static bool IsRecordedProcessRunning(int processId, DateTimeOffset recordedAtUtc)
  {
    try
    {
      using var process = Process.GetProcessById(processId);
      try
      {
        if (process.HasExited) return false;
        return process.StartTime.ToUniversalTime() <= recordedAtUtc.UtcDateTime + ProcessStartTolerance;
      }
      catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
      {
        return true;
      }
    }
    catch (ArgumentException)
    {
      return false;
    }
    catch (Exception)
    {
      return true;
    }
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
