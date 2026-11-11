using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// One line per write request in a local text file, so that it can be looked up
/// later what ran on which drawing and how it ended. The line holds the
/// caller's short description and a hash of the code, never the code or the
/// result. Writing the journal is best effort and never fails an operation.
/// </summary>
internal static class WriteJournal
{
  internal const string DirectoryEnvironmentVariable = "CIVIL3D_MCP_JOURNAL_DIR";
  private const int AppendAttempts = 5;
  private static readonly object Sync = new();

  internal static string ResolveDirectory()
  {
    var configured = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
    if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured))
    {
      return Path.GetFullPath(configured);
    }

    return Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "Civil3dMcpRoslyn",
      "journal");
  }

  /// <summary>One file per month keeps each file small without ever deleting history.</summary>
  internal static string ResolveFilePath(DateTimeOffset nowUtc)
    => Path.Combine(
      ResolveDirectory(),
      nowUtc.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");

  internal static void Append(ExecutionRecord record, string? errorCode, double durationMs)
  {
    try
    {
      var nowUtc = DateTimeOffset.UtcNow;
      var line = new JsonObject
      {
        ["timeUtc"] = nowUtc.ToString("o", CultureInfo.InvariantCulture),
        ["instanceId"] = PluginRuntime.InstanceId,
        ["processId"] = Environment.ProcessId,
        ["pluginCommit"] = BuildInfo.Commit,
        ["drawing"] = record.DatabaseFilename,
        ["fingerprintGuid"] = record.FingerprintGuid,
        ["description"] = record.Description,
        ["codeSha256"] = record.CodeSha256,
        ["codeBytes"] = record.CodeBytes,
        ["ok"] = errorCode == null,
        ["errorCode"] = errorCode,
        ["committed"] = record.Committed,
        ["saveRequested"] = record.SaveRequested,
        ["saved"] = record.Saved,
        ["requireClean"] = record.RequireClean,
        ["backupPath"] = record.BackupPath,
        ["dbmodBefore"] = record.DbmodBefore,
        ["dbmodAfter"] = record.DbmodAfter,
        ["durationMs"] = Math.Round(durationMs),
      }.ToJsonString(JsonOptions) + "\n";

      var path = ResolveFilePath(nowUtc);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      var bytes = new UTF8Encoding(false).GetBytes(line);
      lock (Sync)
      {
        AppendExclusively(path, bytes);
      }
    }
    catch (Exception)
    {
      // The drawing operation has already finished; its answer must not change.
    }
  }

  /// <summary>
  /// Several Civil 3D instances share the file. Each append holds the file
  /// alone for a moment, so lines from two instances cannot interleave.
  /// </summary>
  private static void AppendExclusively(string path, byte[] bytes)
  {
    for (var attempt = 1; ; attempt++)
    {
      try
      {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes, 0, bytes.Length);
        return;
      }
      catch (IOException) when (attempt < AppendAttempts)
      {
        Thread.Sleep(20 * attempt);
      }
    }
  }

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    WriteIndented = false,
    // Keep drawing paths with accented letters readable in the file.
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
  };
}
