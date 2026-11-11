using System.Globalization;

namespace Civil3DMcpPlugin;

/// <summary>
/// Copies the drawing file as it is on disk before a write runs. The copy is
/// taken from the saved file, which is why a backup is normally combined with
/// the "drawing has no unsaved changes" check.
/// </summary>
internal static class DrawingBackup
{
  internal const string DirectoryEnvironmentVariable = "CIVIL3D_MCP_BACKUP_DIR";
  private const string FailedCode = "CIVIL3D.BACKUP_FAILED";
  private const int MaxNameAttempts = 100;

  /// <summary>Request value, then the environment setting, then the per-user default folder.</summary>
  internal static string ResolveDirectory(string? requestedDirectory)
  {
    if (!string.IsNullOrWhiteSpace(requestedDirectory)) return Path.GetFullPath(requestedDirectory);

    var configured = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
    if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured))
    {
      return Path.GetFullPath(configured);
    }

    return Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "Civil3dMcpRoslyn",
      "backups");
  }

  /// <summary>
  /// Returns the full path of the new copy. Throws a refusal that tells the
  /// caller nothing was run when the copy cannot be made or verified.
  /// </summary>
  internal static string Create(string sourcePath, string? requestedDirectory, DateTime localNow)
  {
    string? destination = null;
    try
    {
      var directory = ResolveDirectory(requestedDirectory);
      Directory.CreateDirectory(directory);

      // The drawing is open in Civil 3D, so every sharing mode must be allowed.
      using var source = new FileStream(
        sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      var expectedLength = source.Length;

      using (var target = OpenNewTarget(directory, sourcePath, localNow, out var createdPath))
      {
        // Only a file this call created may be removed again on failure.
        destination = createdPath;
        source.CopyTo(target);
        target.Flush(flushToDisk: true);
      }

      var written = new FileInfo(destination).Length;
      if (written != expectedLength)
      {
        throw new IOException(
          $"the copy has {written.ToString(CultureInfo.InvariantCulture)} bytes, " +
          $"the drawing file has {expectedLength.ToString(CultureInfo.InvariantCulture)}");
      }

      try
      {
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(sourcePath));
      }
      catch (Exception)
      {
        // The time stamp is a convenience only.
      }
      return destination;
    }
    catch (Exception ex) when (ex is not JsonRpcDispatchException)
    {
      TryDelete(destination);
      throw new JsonRpcDispatchException(
        FailedCode,
        $"The backup copy of the drawing could not be made, so nothing was run: {ex.Message}");
    }
  }

  private static FileStream OpenNewTarget(
    string directory,
    string sourcePath,
    DateTime localNow,
    out string createdPath)
  {
    var name = Path.GetFileNameWithoutExtension(sourcePath);
    var extension = Path.GetExtension(sourcePath);
    var stamp = localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    for (var attempt = 1; ; attempt++)
    {
      var suffix = attempt == 1 ? string.Empty : "-" + attempt.ToString(CultureInfo.InvariantCulture);
      var candidate = Path.Combine(directory, $"{name}.{stamp}{suffix}{extension}");
      try
      {
        // CreateNew never overwrites an earlier copy.
        var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        createdPath = candidate;
        return stream;
      }
      catch (IOException) when (File.Exists(candidate) && attempt < MaxNameAttempts)
      {
        // Another copy was made in the same second; try the next name.
      }
    }
  }

  private static void TryDelete(string? path)
  {
    try
    {
      if (path != null && File.Exists(path)) File.Delete(path);
    }
    catch (Exception)
    {
      // A partial copy that cannot be removed is still named in no receipt.
    }
  }
}
