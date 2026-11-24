using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// The listener answers on the loopback address, which every program and
/// every user on the computer can reach. Each plugin session therefore makes
/// up a random token, writes it into its endpoint record in the user's own
/// profile folder, and runs a request only when the request presents it. The
/// MCP server reads the record and sends the token along; a program that
/// cannot read the user's profile cannot.
/// </summary>
internal static class SessionAuth
{
  /// <summary>
  /// Set to 0 in Civil 3D's environment to accept requests without a token,
  /// for an MCP server from before the token existed.
  /// </summary>
  internal const string RequireEnvironmentVariable = "CIVIL3D_MCP_REQUIRE_TOKEN";

  /// <summary>Top-level member of the request that carries the token.</summary>
  internal const string RequestPropertyName = "auth";

  internal const string RefusalCode = "CIVIL3D.UNAUTHORIZED";

  internal const string RefusalMessage =
    "This Civil 3D session only runs requests that present its session token, and this request did not. " +
    "Nothing was run. The token is in the session's endpoint record, which the MCP server reads by itself: " +
    "update the MCP server to the version of the plugin, or check that both use the same endpoint folder " +
    "(CIVIL3D_MCP_ENDPOINT_DIR).";

  internal static bool IsRequired => !string.Equals(
    Environment.GetEnvironmentVariable(RequireEnvironmentVariable), "0", StringComparison.Ordinal);

  /// <summary>256 random bits as 64 hexadecimal characters.</summary>
  internal static string CreateToken()
    => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

  /// <summary>
  /// True only for the exact token. Compared in constant time, so that the
  /// answer time tells nothing about how much of a guess was right.
  /// </summary>
  internal static bool Matches(string? expected, JsonNode? presented)
  {
    if (string.IsNullOrEmpty(expected)) return false;
    if (presented is not JsonValue value || !value.TryGetValue<string>(out var text) || text == null)
    {
      return false;
    }
    return CryptographicOperations.FixedTimeEquals(
      Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(text));
  }
}
