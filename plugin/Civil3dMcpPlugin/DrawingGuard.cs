using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

internal sealed record ExpectedDrawing(string DatabaseFilename, Guid FingerprintGuid);

internal static class DrawingGuard
{
  private const string RequiredCode = "CIVIL3D.DRAWING_GUARD_REQUIRED";
  private const string InvalidCode = "CIVIL3D.DRAWING_GUARD_INVALID";
  private const string MismatchCode = "CIVIL3D.DRAWING_MISMATCH";

  public static ExpectedDrawing? Parse(JsonObject? parameters, bool required)
  {
    if (parameters == null || !parameters.ContainsKey("expectedDrawing"))
    {
      if (required)
      {
        throw new JsonRpcDispatchException(
          RequiredCode,
          "Parameter 'expectedDrawing' is required when readOnly is false."
        );
      }
      return null;
    }

    if (
      parameters["expectedDrawing"] is not JsonObject value ||
      value.Count != 2 ||
      !value.ContainsKey("databaseFilename") ||
      !value.ContainsKey("fingerprintGuid") ||
      !TryGetString(value, "databaseFilename", out var databaseFilename) ||
      !TryGetString(value, "fingerprintGuid", out var fingerprintText) ||
      !TryNormalizeExpectedPath(databaseFilename, out var normalizedFilename) ||
      !Guid.TryParse(fingerprintText, out var fingerprintGuid)
    )
    {
      throw InvalidGuard();
    }

    return new ExpectedDrawing(normalizedFilename, fingerprintGuid);
  }

  public static T ValidateThenRun<T>(
    ExpectedDrawing? expectedDrawing,
    string actualDatabaseFilename,
    string actualFingerprintGuid,
    Func<T> action)
  {
    Validate(expectedDrawing, actualDatabaseFilename, actualFingerprintGuid);
    return action();
  }

  public static void Validate(
    ExpectedDrawing? expectedDrawing,
    string actualDatabaseFilename,
    string actualFingerprintGuid)
  {
    if (expectedDrawing == null) return;

    var actualFilename = NormalizeActualPath(actualDatabaseFilename);
    var actualFingerprintIsValid = Guid.TryParse(
      actualFingerprintGuid,
      out var parsedActualFingerprintGuid
    );
    if (
      actualFilename == null ||
      !string.Equals(
        expectedDrawing.DatabaseFilename,
        actualFilename,
        StringComparison.OrdinalIgnoreCase
      ) ||
      !actualFingerprintIsValid ||
      expectedDrawing.FingerprintGuid != parsedActualFingerprintGuid
    )
    {
      throw new JsonRpcDispatchException(
        MismatchCode,
        "The active drawing does not match the supplied expectedDrawing identity."
      );
    }
  }

  private static bool TryGetString(JsonObject value, string name, out string result)
  {
    if (
      value[name] is JsonValue node &&
      node.TryGetValue<string>(out var parsed) &&
      parsed != null
    )
    {
      result = parsed;
      return true;
    }

    result = string.Empty;
    return false;
  }

  private static bool TryNormalizeExpectedPath(string value, out string normalized)
  {
    if (value.Length == 0)
    {
      normalized = string.Empty;
      return true;
    }

    if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
    {
      normalized = string.Empty;
      return false;
    }

    try
    {
      normalized = NormalizeFullPath(value);
      return true;
    }
    catch (Exception ex) when (
      ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
      normalized = string.Empty;
      return false;
    }
  }

  private static string? NormalizeActualPath(string value)
  {
    if (value.Length == 0) return string.Empty;
    if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return null;

    try
    {
      return NormalizeFullPath(value);
    }
    catch (Exception ex) when (
      ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return null;
    }
  }

  private static string NormalizeFullPath(string value)
  {
    var normalized = Path.GetFullPath(value);
    if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
    {
      normalized = @"\\" + normalized[8..];
    }
    else if (normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
    {
      normalized = normalized[4..];
    }
    return Path.TrimEndingDirectorySeparator(normalized);
  }

  private static JsonRpcDispatchException InvalidGuard()
    => new(
      InvalidCode,
      "Parameter 'expectedDrawing' must contain exactly an absolute databaseFilename " +
      "(or an empty string for an unsaved drawing) and a valid fingerprintGuid."
    );
}
