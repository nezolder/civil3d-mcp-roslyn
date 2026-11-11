using System.Diagnostics;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>Identity of the drawing that was active when it was last looked at.</summary>
internal sealed record ActiveDrawingSnapshot(
  string DatabaseFilename,
  string FingerprintGuid,
  DateTimeOffset ObservedAtUtc);

/// <summary>One look at the application: the active drawing, or null when none is open.</summary>
internal sealed record ActiveDrawingObservation(ActiveDrawingSnapshot? Drawing);

/// <summary>
/// Remembers which drawing is active so that the health reply can say so
/// without entering a command. The MCP host uses it only to choose between
/// several running Civil 3D instances; the drawing guard still checks the real
/// active drawing immediately before any Civil API access.
///
/// The drawing is read on the main thread only: when the application becomes
/// idle (at most a few times per second) and after each MCP operation.
/// </summary>
internal static class ActiveDrawingTracker
{
  private static readonly long RefreshIntervalTicks = Stopwatch.Frequency / 4;
  private static ActiveDrawingObservation? _observation;
  private static long _lastRefreshTimestamp;
  private static bool _initialized;

  internal static void Initialize()
  {
    if (_initialized) return;
    _initialized = true;
    App.Idle += OnIdle;
  }

  internal static void Terminate()
  {
    if (!_initialized) return;
    App.Idle -= OnIdle;
    _initialized = false;
  }

  /// <summary>Null until the application has been looked at once.</summary>
  internal static ActiveDrawingObservation? Current => Volatile.Read(ref _observation);

  private static void OnIdle(object? sender, EventArgs args)
  {
    var last = Volatile.Read(ref _lastRefreshTimestamp);
    if (last != 0 && Stopwatch.GetTimestamp() - last < RefreshIntervalTicks) return;
    Refresh();
  }

  /// <summary>Main thread only.</summary>
  internal static void Refresh()
  {
    Volatile.Write(ref _lastRefreshTimestamp, Stopwatch.GetTimestamp());
    try
    {
      var database = App.DocumentManager.MdiActiveDocument?.Database;
      var previous = Volatile.Read(ref _observation);
      if (database == null)
      {
        if (previous == null || previous.Drawing != null)
        {
          Volatile.Write(ref _observation, new ActiveDrawingObservation(null));
        }
        return;
      }

      var filename = database.Filename ?? string.Empty;
      var fingerprint = database.FingerprintGuid?.ToString() ?? string.Empty;
      // Keep the object when nothing changed: Idle fires very often.
      if (previous?.Drawing != null
        && string.Equals(previous.Drawing.DatabaseFilename, filename, StringComparison.Ordinal)
        && string.Equals(previous.Drawing.FingerprintGuid, fingerprint, StringComparison.Ordinal))
      {
        return;
      }
      Volatile.Write(
        ref _observation,
        new ActiveDrawingObservation(
          new ActiveDrawingSnapshot(filename, fingerprint, DateTimeOffset.UtcNow)));
    }
    catch (Exception)
    {
      // A drawing that is just opening or closing may not be readable yet.
      // Keep the previous answer; the next idle moment tries again.
    }
  }

  internal static void ResetForTests()
  {
    Volatile.Write(ref _observation, null);
    Volatile.Write(ref _lastRefreshTimestamp, 0);
  }
}
