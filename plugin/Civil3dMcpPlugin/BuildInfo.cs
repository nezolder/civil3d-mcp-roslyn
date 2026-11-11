using System.Reflection;

namespace Civil3DMcpPlugin;

/// <summary>
/// Which source state this plugin DLL was built from. The project file stamps
/// the commit and build time into the assembly (see Civil3dMcpPlugin.csproj);
/// a build without git reports "unknown".
/// </summary>
internal static class BuildInfo
{
  internal const string CommitKey = "Civil3dMcpCommit";
  internal const string DirtyKey = "Civil3dMcpDirty";
  internal const string BuiltAtKey = "Civil3dMcpBuiltAtUtc";
  internal const string Unknown = "unknown";

  internal static string Commit { get; } = Read(CommitKey) ?? Unknown;

  /// <summary>
  /// True when tracked files had uncommitted changes at build time, false when
  /// they had none, null when that could not be determined.
  /// </summary>
  internal static bool? Dirty { get; } = Read(DirtyKey)?.ToLowerInvariant() switch
  {
    "true" => true,
    "false" => false,
    _ => null,
  };

  internal static string? BuiltAtUtc { get; } = Read(BuiltAtKey);

  private static string? Read(string key)
  {
    try
    {
      var value = typeof(BuildInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == key)?.Value;
      return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
    catch (Exception)
    {
      return null;
    }
  }
}
