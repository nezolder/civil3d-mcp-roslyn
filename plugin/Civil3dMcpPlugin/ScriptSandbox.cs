using System.Text.RegularExpressions;

namespace Civil3DMcpPlugin;

/// <summary>
/// Security sandbox that validates C# code before execution.
/// Blocks potentially dangerous operations while allowing full Civil 3D API access.
/// </summary>
public static class ScriptSandbox
{
  /// <summary>Patterns that indicate dangerous operations.</summary>
  private static readonly (string Pattern, string Reason)[] BlockedPatterns = new[]
  {
    (@"System\.Diagnostics\.Process", "Process execution is not allowed"),
    (@"System\.IO\.File\.Delete", "File deletion is not allowed"),
    (@"System\.IO\.Directory\.Delete", "Directory deletion is not allowed"),
    (@"System\.Net\.Http", "HTTP requests are not allowed"),
    (@"System\.Net\.Sockets", "Socket operations are not allowed"),
    (@"System\.Reflection\.Assembly\.Load", "Dynamic assembly loading is not allowed"),
    (@"System\.Runtime\.InteropServices", "P/Invoke is not allowed"),
    (@"Environment\.Exit", "Process termination is not allowed"),
    (@"Registry\.", "Registry access is not allowed"),
    (@"Process\.Start", "Process execution is not allowed"),
    (@"AppDomain\.CreateDomain", "Creating AppDomains is not allowed"),
  };

  /// <summary>
  /// Validate code against the sandbox rules.
  /// Throws JsonRpcDispatchException if blocked patterns are found.
  /// </summary>
  public static void Validate(string code)
  {
    if (string.IsNullOrWhiteSpace(code))
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        "Code cannot be empty."
      );
    }

    foreach (var (pattern, reason) in BlockedPatterns)
    {
      if (Regex.IsMatch(code, pattern, RegexOptions.IgnoreCase))
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.SANDBOX_VIOLATION",
          $"Script blocked: {reason}. Pattern: {pattern}"
        );
      }
    }
  }
}
