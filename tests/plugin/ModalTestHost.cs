using System.Collections.Concurrent;
using Autodesk.AutoCAD.ApplicationServices;

namespace Civil3DMcpPlugin;

// A deterministic host double for backend-independent contracts. A dedicated
// thread processes Idle, the registered entry method and a separate host-end
// event. It does not model actual AutoCAD selection, Undo or scheduling latency.
internal sealed class ModalTestHost : IDisposable
{
  private readonly ConcurrentQueue<(Document Document, string Token)> _commands = new();
  private readonly ManualResetEventSlim _stop = new();
  private readonly Thread _thread;
  private Exception? _failure;

  internal ModalTestHost()
  {
    ModalCommandAdmission.Initialize();
    _thread = new Thread(Pump) { IsBackground = true, Name = "Modal contract test host" };
    _thread.Start();
  }

  internal void Attach(Document document)
    => document.CommandQueued = (doc, token) => _commands.Enqueue((doc, token));

  private void Pump()
  {
    try
    {
      while (!_stop.IsSet)
      {
        Application.RaiseIdle();
        while (_commands.TryDequeue(out var command))
        {
          command.Document.Editor.CommandToken = command.Token;
          new PluginEntry().RunModalCommand();
          command.Document.EndCommand(ModalCommandAdmission.CommandName);
        }
        _stop.Wait(1);
      }
      Application.RaiseIdle();
    }
    catch (Exception error) { _failure = error; }
  }

  public void Dispose()
  {
    _stop.Set();
    if (!_thread.Join(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Modal test host did not stop.");
    ModalCommandAdmission.Terminate();
    _stop.Dispose();
    if (_failure != null) throw new InvalidOperationException("Modal test host failed.", _failure);
  }
}
