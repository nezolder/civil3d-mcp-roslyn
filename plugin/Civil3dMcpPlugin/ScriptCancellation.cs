using System.ComponentModel;

namespace Civil3DMcpPlugin;

/// <summary>
/// Cancellation checkpoint that <see cref="ScriptInstrumentation"/> inserts at
/// the top of every loop body in caller scripts. A runaway loop then stops at
/// the script timeout or when the client disconnects, instead of holding the
/// Civil 3D main thread and the operation gate indefinitely.
/// </summary>
public static class ScriptCancellation
{
  private static readonly AsyncLocal<CancellationToken> Current = new();

  /// <summary>Called by instrumented script loops; not meant for direct use.</summary>
  [EditorBrowsable(EditorBrowsableState.Never)]
  public static void ThrowIfCancellationRequested() => Current.Value.ThrowIfCancellationRequested();

  internal static CancellationToken Token
  {
    get => Current.Value;
    set => Current.Value = value;
  }
}
