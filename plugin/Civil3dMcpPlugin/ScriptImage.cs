using System.Buffers.Binary;
using System.Globalization;

namespace Civil3DMcpPlugin;

/// <summary>
/// An image file that a script hands back with its result, created with
/// <c>ImageFile(path)</c>. The file is read when the result is serialized and
/// travels next to the JSON result as an image, so the assistant can look at
/// a plotted view or sheet. In the JSON it leaves a small reference:
/// <c>{"image": 1, "mimeType": "image/png", "bytes": 81234, "width": 1600, "height": 1280}</c>.
/// </summary>
public sealed class ScriptImage
{
  internal ScriptImage(string path) => Path = path;

  /// <summary>Full path of the PNG or JPEG file.</summary>
  public string Path { get; }
}

/// <summary>One image as it is sent: its type and its bytes.</summary>
internal sealed record ScriptImagePayload(string MimeType, byte[] Data);

/// <summary>What is known about an image file from its first bytes.</summary>
internal sealed record ScriptImageInfo(string MimeType, int? Width, int? Height);

internal static class ScriptImages
{
  /// <summary>Images per result. Each one costs the reader about as much as a page of text.</summary>
  internal const int MaxImagesPerResult = 4;

  /// <summary>Largest single image file.</summary>
  internal const int MaxImageBytes = 2 * 1024 * 1024;

  /// <summary>
  /// All images of one result together. As base64 this stays well below the
  /// transport's limit for one reply, so an image can never turn a finished
  /// operation into a reply that is too large to return.
  /// </summary>
  internal const int MaxTotalImageBytes = 4 * 1024 * 1024;

  internal const string DirectoryEnvironmentVariable = "CIVIL3D_MCP_IMAGE_DIR";

  /// <summary>Files in the plugin's own image folder are removed after this time.</summary>
  internal static readonly TimeSpan KeepFor = TimeSpan.FromHours(24);

  private static readonly string[] OwnExtensions = { ".png", ".jpg", ".jpeg", ".pdf" };

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
      "images");
  }

  /// <summary>
  /// A new file path in the plugin's own image folder. The folder is the
  /// plugin's scratch space: files older than <see cref="KeepFor"/> are
  /// removed here, so a script never has to delete what it plotted.
  /// </summary>
  internal static string NewPath(string? extension, DateTime now)
  {
    var cleaned = (extension ?? "png").Trim().TrimStart('.').ToLowerInvariant();
    if (cleaned.Length == 0 || !OwnExtensions.Contains("." + cleaned, StringComparer.Ordinal))
    {
      throw new ArgumentException("The extension must be png, jpg, jpeg or pdf.", nameof(extension));
    }

    var directory = ResolveDirectory();
    Directory.CreateDirectory(directory);
    Prune(directory, now);
    var name = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
      + "-" + Guid.NewGuid().ToString("N")[..8] + "." + cleaned;
    return Path.Combine(directory, name);
  }

  /// <summary>Best effort; a file that cannot be removed now is tried again next time.</summary>
  internal static void Prune(string directory, DateTime now)
  {
    try
    {
      foreach (var file in Directory.EnumerateFiles(directory))
      {
        try
        {
          if (!OwnExtensions.Contains(Path.GetExtension(file).ToLowerInvariant(), StringComparer.Ordinal)) continue;
          if (now - File.GetLastWriteTime(file) > KeepFor) File.Delete(file);
        }
        catch (Exception)
        {
          // In use or protected: leave it.
        }
      }
    }
    catch (Exception)
    {
      // The folder itself could not be read; nothing to tidy.
    }
  }

  /// <summary>PNG or JPEG by content, never by file name. Null for anything else.</summary>
  internal static ScriptImageInfo? Inspect(ReadOnlySpan<byte> data)
  {
    ReadOnlySpan<byte> pngSignature = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    if (data.Length >= 24 && data[..8].SequenceEqual(pngSignature))
    {
      // The IHDR chunk comes first: length, "IHDR", width, height.
      var width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
      var height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
      return width > 0 && height > 0
        ? new ScriptImageInfo("image/png", width, height)
        : new ScriptImageInfo("image/png", null, null);
    }
    if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
    {
      return new ScriptImageInfo("image/jpeg", null, null);
    }
    return null;
  }
}
