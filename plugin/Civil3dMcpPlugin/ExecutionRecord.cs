using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// What one executeCode request asked for and what happened to the drawing.
/// It feeds the receipt returned next to a write result and the local write
/// journal. It never holds the caller's code or the script result.
/// </summary>
internal sealed class ExecutionRecord
{
  internal const int MaxDescriptionLength = 300;

  // ── Request ──
  internal bool IsWrite { get; private set; }
  internal bool RequireClean { get; set; }
  /// <summary>The caller did not ask for the check itself; a backup request brought it along.</summary>
  internal bool RequireCleanFromBackup { get; set; }
  internal bool Backup { get; set; }
  internal string? BackupDirectory { get; set; }
  internal bool SaveRequested { get; set; }
  internal string? Description { get; private set; }
  internal string? CodeSha256 { get; private set; }
  internal int CodeBytes { get; private set; }

  // ── Outcome ──
  internal string? DatabaseFilename { get; private set; }
  internal string? FingerprintGuid { get; private set; }
  internal int? DbmodBefore { get; set; }
  internal int? DbmodAfter { get; set; }
  internal bool Committed { get; set; }
  internal bool Saved { get; set; }
  internal string? BackupPath { get; set; }
  internal DateTimeOffset? FileLastWriteUtc { get; private set; }
  internal long? FileBytes { get; private set; }

  /// <summary>True once the active drawing was resolved, so a receipt has something to say.</summary>
  internal bool ReachedDrawing => DatabaseFilename != null;

  /// <summary>
  /// Reads what can be known before validation. Never throws: an invalid
  /// request is refused later by the dispatcher and is still journaled.
  /// </summary>
  internal static ExecutionRecord FromRequest(JsonObject? parameters)
  {
    var record = new ExecutionRecord();
    try
    {
      var readOnly = parameters?["readOnly"] is JsonValue readOnlyValue
        && readOnlyValue.TryGetValue<bool>(out var parsedReadOnly)
        && parsedReadOnly;
      record.IsWrite = !readOnly;
      if (parameters?["code"] is JsonValue codeValue && codeValue.TryGetValue<string>(out var code) && code != null)
      {
        var bytes = Encoding.UTF8.GetBytes(code);
        record.CodeBytes = bytes.Length;
        record.CodeSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
      }
      if (parameters?["description"] is JsonValue descriptionValue
        && descriptionValue.TryGetValue<string>(out var description))
      {
        record.Description = ToSingleLine(description, MaxDescriptionLength);
      }
    }
    catch (Exception)
    {
      // The record is best effort; the request itself is validated elsewhere.
    }
    return record;
  }

  internal void RecordDrawing(string? databaseFilename, string? fingerprintGuid)
  {
    DatabaseFilename = databaseFilename ?? string.Empty;
    FingerprintGuid = fingerprintGuid ?? string.Empty;
  }

  /// <summary>Size and time of the drawing file as it is on disk now. Best effort.</summary>
  internal void RecordFile(string? path)
  {
    try
    {
      if (string.IsNullOrWhiteSpace(path)) return;
      var file = new FileInfo(path);
      if (!file.Exists) return;
      FileLastWriteUtc = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
      FileBytes = file.Length;
    }
    catch (Exception)
    {
      // An unreachable network drive must not turn a finished write into an error.
    }
  }

  internal JsonObject ToReceipt() => new()
  {
    ["drawing"] = new JsonObject
    {
      ["databaseFilename"] = DatabaseFilename,
      ["fingerprintGuid"] = FingerprintGuid,
    },
    ["dbmodBefore"] = DbmodBefore,
    ["dbmodAfter"] = DbmodAfter,
    ["committed"] = Committed,
    ["saved"] = Saved,
    ["fileLastWriteUtc"] = FileLastWriteUtc?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
    ["fileBytes"] = FileBytes,
    ["backupPath"] = BackupPath,
  };

  /// <summary>Collapses line breaks and control characters, trims, and cuts to the limit.</summary>
  internal static string? ToSingleLine(string? value, int maxLength)
  {
    if (string.IsNullOrWhiteSpace(value)) return null;
    var builder = new StringBuilder(Math.Min(value.Length, maxLength));
    var previousWasSpace = true;
    foreach (var character in value)
    {
      var isSpace = char.IsWhiteSpace(character) || char.IsControl(character);
      if (isSpace)
      {
        if (!previousWasSpace) builder.Append(' ');
      }
      else
      {
        builder.Append(character);
      }
      previousWasSpace = isSpace;
      if (builder.Length >= maxLength) break;
    }
    var text = builder.ToString().Trim();
    return text.Length == 0 ? null : text;
  }
}
