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
    if (!CivilExecution.UseNativeBackend) ModalCommandAdmission.Initialize();
    ActiveDrawingTracker.Initialize();
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
    ActiveDrawingTracker.Terminate();
    ModalCommandAdmission.Terminate();
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

  /// <summary>
  /// Release an operation that is stuck although Civil 3D is idle. Meant to
  /// be typed by the person at the computer; it cannot interrupt a script
  /// that is still running, because no command starts while one runs.
  /// </summary>
  [CommandMethod("C3DMCPRESET")]
  public void ResetCommand()
  {
    var report = ModalCommandAdmission.ReleaseByOperator();
    var released = report.NotStarted + report.Finished;
    if (released == 0 && report.StillRunning == 0)
    {
      var status = PluginRuntime.GetStatus();
      WriteMessage(status.OperationInProgress
        ? "Civil3D MCP: an operation is marked active, but it is not one this command can release. " +
          "Restart Civil 3D if it does not finish."
        : "Civil3D MCP: nothing is stuck; nothing was released.");
      return;
    }
    if (report.NotStarted > 0)
    {
      WriteMessage($"Civil3D MCP: released {report.NotStarted} waiting request(s) that had not started; nothing ran.");
    }
    if (report.Finished > 0)
    {
      WriteMessage($"Civil3D MCP: released {report.Finished} operation(s) whose script had finished. " +
        "Check the drawing before repeating anything.");
    }
    if (report.StillRunning > 0)
    {
      WriteMessage($"Civil3D MCP: {report.StillRunning} operation(s) are still running and were left alone.");
    }
  }

  // Internal native commands, not additional public MCP tools. Only an opaque
  // one-use request token enters AutoCAD's command input.
  [CommandMethod(ModalCommandAdmission.CommandName,
    CommandFlags.Modal | CommandFlags.NoHistory | CommandFlags.UsePickSet | CommandFlags.Redraw)]
  public void RunModalCommand() => ModalCommandAdmission.RunCommand();

  // The same body for reads, registered without an Undo marker: a query
  // commits nothing, so it must not add a step to the user's Undo list.
  [CommandMethod(ModalCommandAdmission.ReadCommandName,
    CommandFlags.Modal | CommandFlags.NoHistory | CommandFlags.NoUndoMarker
      | CommandFlags.UsePickSet | CommandFlags.Redraw)]
  public void RunModalReadCommand() => ModalCommandAdmission.RunReadCommand();

  private static void WriteMessage(string message)
  {
    var doc = App.DocumentManager.MdiActiveDocument;
    doc?.Editor.WriteMessage($"\n{message}");
  }
}
