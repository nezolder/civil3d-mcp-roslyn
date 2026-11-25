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
  /// <summary>
  /// The script runs with write access, but its transaction is abandoned: no
  /// commit, no save, no backup. The receipt lists what it would have changed.
  /// </summary>
  internal bool Preview { get; private set; }
  /// <summary>How many objects of each kind the receipt lists by handle and name.</summary>
  internal int ChangeItemLimit { get; set; } = ChangeList.DefaultItemLimit;
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
  /// <summary>What the write added, modified and erased; null when it was not looked at.</summary>
  internal ChangeList? Changes { get; set; }
  /// <summary>What the saves after the command found; null when no save was settled.</summary>
  internal SaveSettleReport? SaveSettle { get; set; }
  internal DateTimeOffset? FileLastWriteUtc { get; private set; }
  internal long? FileBytes { get; private set; }

  /// <summary>Notes on API members the script uses; sent next to the result, never journaled.</summary>
  internal List<ApiNote> Notes { get; } = new();

  /// <summary>Image files the script returned; sent next to the result, never journaled.</summary>
  internal List<ScriptImagePayload> Images { get; } = new();

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
      record.Preview = !readOnly
        && parameters?["preview"] is JsonValue previewValue
        && previewValue.TryGetValue<bool>(out var parsedPreview)
        && parsedPreview;
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

  internal JsonObject ToReceipt()
  {
    var receipt = new JsonObject
    {
      ["drawing"] = new JsonObject
      {
        ["databaseFilename"] = DatabaseFilename,
        ["fingerprintGuid"] = FingerprintGuid,
      },
      ["dbmodBefore"] = DbmodBefore,
      ["dbmodAfter"] = DbmodAfter,
      ["preview"] = Preview,
      ["committed"] = Committed,
      ["saved"] = Saved,
      ["fileLastWriteUtc"] = FileLastWriteUtc?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
      ["fileBytes"] = FileBytes,
      ["backupPath"] = BackupPath,
    };
    if (SaveSettle != null) receipt["saveSettle"] = SaveSettle.ToJson();
    if (Changes != null) receipt["changes"] = Changes.ToJson();
    return receipt;
  }

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

/// <summary>
/// What happened after the command of a saved write: how often DBMOD was read
/// and the drawing saved again, and whether it ended clean. Civil 3D can mark
/// a drawing modified again just after a save when it updates objects that
/// depend on the written ones.
/// </summary>
internal sealed class SaveSettleReport
{
  internal const string DrawingChanged = "activeDrawingChanged";
  internal const string DbmodUnreadable = "dbmodUnreadable";
  internal const string LimitReached = "extraSaveLimitReached";
  internal const string SaveFailed = "extraSaveFailed";
  internal const string StepNotRun = "stepDidNotRun";

  /// <summary>DBMOD read right after the first save, inside the write's command.</summary>
  internal int? DbmodAfterFirstSave { get; set; }
  internal int Checks { get; set; }
  internal int ExtraSaves { get; set; }
  internal int? LastDbmod { get; set; }
  internal bool Clean { get; set; }
  /// <summary>Why it stopped before the drawing was seen clean; null when it ended clean.</summary>
  internal string? Stopped { get; set; }
  internal string? SaveError { get; set; }

  internal JsonObject ToJson()
  {
    var json = new JsonObject
    {
      ["clean"] = Clean,
      ["checks"] = Checks,
      ["extraSaves"] = ExtraSaves,
      ["dbmodAfterFirstSave"] = DbmodAfterFirstSave,
    };
    if (Stopped != null) json["stopped"] = Stopped;
    if (SaveError != null) json["saveError"] = SaveError;
    return json;
  }
}
