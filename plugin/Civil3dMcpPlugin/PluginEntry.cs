using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(Civil3DMcpPlugin.PluginEntry))]
[assembly: CommandClass(typeof(Civil3DMcpPlugin.PluginEntry))]

namespace Civil3DMcpPlugin;

/// <summary>
/// Entry point for the Civil 3D MCP plugin.
/// Loads automatically when Civil 3D starts (via NETLOAD) and starts
/// a TCP socket server that listens for JSON-RPC commands from the MCP server.
/// </summary>
public sealed class PluginEntry : IExtensionApplication
{
  public void Initialize()
  {
    try
    {
      PluginRuntime.StartServer();
      WriteMessage("Civil3D MCP plugin initialized.");
    }
    catch (System.Exception ex)
    {
      WriteMessage($"Civil3D MCP plugin failed to initialize: {ex.Message}");
    }
  }

  public void Terminate()
  {
    PluginRuntime.StopServer();
  }

  /// <summary>Manually start the MCP TCP listener.</summary>
  [CommandMethod("C3DMCPSTART")]
  public void StartCommand()
  {
    PluginRuntime.StartServer();
    WriteMessage($"Civil3D MCP listener started on port {PluginRuntime.Port}.");
  }

  /// <summary>Manually stop the MCP TCP listener.</summary>
  [CommandMethod("C3DMCPSTOP")]
  public void StopCommand()
  {
    PluginRuntime.StopServer();
    WriteMessage("Civil3D MCP listener stopped.");
  }

  /// <summary>Check the status of the MCP TCP listener.</summary>
  [CommandMethod("C3DMCPSTATUS")]
  public void StatusCommand()
  {
    var status = PluginRuntime.GetStatus();
    WriteMessage(
      $"Civil3D MCP listener running: {status.IsRunning}; " +
      $"pending: {status.QueueDepth}; " +
      $"active: {status.OperationInProgress}; " +
      $"current: {status.CurrentOperation ?? "<none>"}"
    );
  }

  private static void WriteMessage(string message)
  {
    var doc = App.DocumentManager.MdiActiveDocument;
    doc?.Editor.WriteMessage($"\n{message}");
  }
}
