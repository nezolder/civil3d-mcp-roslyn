using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Takes the "modified" mark (DBMOD) off a drawing after a request that kept
/// nothing. An abandoned transaction leaves the drawing as it was, but
/// AutoCAD still marks it as modified (seen live: a preview on a clean drawing
/// raised DBMOD from 0 to 1). The next write that asks for a clean drawing,
/// or for a backup, would then be refused although nothing has changed.
///
/// DBMOD is read-only for managed code. The database library exports
/// <c>acdbSetDbmod(AcDbDatabase*, value)</c> for this purpose; it is looked up
/// by name in the library this process has loaded, so no version number is
/// fixed here. When it cannot be found or fails, the mark simply stays.
/// </summary>
internal static class DrawingModifiedFlag
{
  // acdb25.dll (Civil 3D 2025) exports the int form; the long form is the
  // name older releases used. Both take and return a 32-bit value on x64.
  private static readonly string[] ExportNames =
  {
    "?acdbSetDbmod@@YAHPEAVAcDbDatabase@@H@Z",
    "?acdbSetDbmod@@YAJPEAVAcDbDatabase@@J@Z",
  };
  private static readonly Regex DatabaseLibraryName = new(@"^acdb\d+\.dll$", RegexOptions.IgnoreCase);

  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
  private delegate int SetDbmod(IntPtr database, int newValue);

  private static readonly object Sync = new();
  private static SetDbmod? _setDbmod;
  private static bool _lookedUp;

  /// <summary>
  /// Marks the drawing as not modified. Call only when the drawing was clean
  /// before the request and the request verifiably kept nothing.
  /// </summary>
  internal static bool TryClear(Database database)
  {
    try
    {
      var setDbmod = Find();
      if (setDbmod == null) return false;
      var pointer = database.UnmanagedObject;
      if (pointer == IntPtr.Zero) return false;
      setDbmod(pointer, 0);
      return true;
    }
    catch (Exception)
    {
      return false;
    }
  }

  private static SetDbmod? Find()
  {
    lock (Sync)
    {
      if (_lookedUp) return _setDbmod;
      _lookedUp = true;
      try
      {
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
          if (!DatabaseLibraryName.IsMatch(module.ModuleName ?? string.Empty)) continue;
          foreach (var exportName in ExportNames)
          {
            if (NativeLibrary.TryGetExport(module.BaseAddress, exportName, out var address) && address != IntPtr.Zero)
            {
              _setDbmod = Marshal.GetDelegateForFunctionPointer<SetDbmod>(address);
              return _setDbmod;
            }
          }
        }
      }
      catch (Exception)
      {
        _setDbmod = null;
      }
      return _setDbmod;
    }
  }
}
