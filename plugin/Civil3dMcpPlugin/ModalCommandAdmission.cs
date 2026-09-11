using System.Collections.Concurrent;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

// Enters a real modal command instead of awaiting Autodesk's ExecutionResult.
// The existing operation gate remains held until this request's host command ends.
internal static class ModalCommandAdmission
{
  internal const string CommandName = "C3DMCPRUN";
  private static readonly ConcurrentDictionary<string, Request> Requests = new();
  private static readonly ConcurrentQueue<Action> Cleanup = new();
  private static bool _initialized;
  internal static TimeSpan? StartDeadlineOverrideForTests { get; set; }

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

  internal static async Task ExecuteAsync(Action body, CancellationToken cancellationToken, Func<bool> committed)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var request = new Request(body, committed);
    Requests[request.Token] = request;
    using var deadline = new CancellationTokenSource(StartDeadlineOverrideForTests ?? TimeSpan.FromSeconds(15));
    using var registration = cancellationToken.Register(() =>
      request.Abandon(new OperationCanceledException(cancellationToken)));
    using var deadlineRegistration = deadline.Token.Register(() =>
      request.Abandon(new JsonRpcDispatchException("CIVIL3D.COMMAND_CONTEXT_TIMEOUT",
        "The modal command did not start within 15 seconds; the script did not run.")));
    try { await request.Completion.Task.ConfigureAwait(false); }
    finally
    {
      Requests.TryRemove(request.Token, out _);
      Cleanup.Enqueue(request.Detach);
    }
  }

  private static void OnIdle(object? sender, EventArgs args)
  {
    while (Cleanup.TryDequeue(out var cleanup)) cleanup();
    if (Requests.IsEmpty) return;
    foreach (var request in Requests.Values)
    {
      if (!request.CanQueue) continue;
      var doc = App.DocumentManager.MdiActiveDocument;
      if (doc == null) { request.Abandon(new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing.")); continue; }
      if (!doc.Editor.IsQuiescent) continue;
      try { request.Queue(doc); }
      catch (Exception ex) { request.Abandon(ex); }
    }
  }

  // Called only by a genuine modal CommandMethod, never by the async context API.
  internal static void RunCommand()
  {
    var doc = App.DocumentManager.MdiActiveDocument;
    if (doc == null) return;
    var token = doc.Editor.GetString(new PromptStringOptions("\nMCP request: ") { AllowSpaces = false });
    if (token.Status != PromptStatus.OK || !Requests.TryGetValue(token.StringResult, out var request)) return;
    request.Run(doc);
  }

  private sealed class Request
  {
    private readonly object _sync = new();
    private Action? _body;
    private readonly Func<bool> _committed;
    private Document? _document;
    private bool _queued;
    private bool _started;
    private bool _bodyExited;
    private bool _abandoned;
    private Exception? _error;
    internal string Token { get; } = Guid.NewGuid().ToString("N");
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Request(Action body, Func<bool> committed) { _body = body; _committed = committed; }
    internal bool CanQueue { get { lock (_sync) return !_queued && !_abandoned; } }

    internal void Queue(Document doc)
    {
      lock (_sync)
      {
        if (_queued || _abandoned) return;
        _queued = true;
        _document = doc;
      }
      doc.CommandEnded += OnEnded;
      doc.CommandCancelled += OnCancelled;
      doc.CommandFailed += OnCancelled;
      doc.SendStringToExecute(CommandName + " " + Token + "\n", false, false, false);
    }

    internal void Run(Document doc)
    {
      Action? body;
      lock (_sync)
      {
        if (_started || _abandoned) return;
        if (!ReferenceEquals(doc, _document))
        {
          Abandon(new JsonRpcDispatchException("CIVIL3D.DRAWING_MISMATCH", "The modal request belongs to a different document."));
          return;
        }
        _started = true;
        body = _body;
        _body = null;
      }
      try { body!(); }
      catch (Exception ex) { _error = ex; }
      finally { lock (_sync) _bodyExited = true; }
      // No completion here: the host's own command-end event is authoritative.
    }

    internal void Abandon(Exception error)
    {
      lock (_sync)
      {
        if (_started || _abandoned) return;
        _abandoned = true;
        _body = null;
        Completion.TrySetException(error);
      }
    }

    private void OnEnded(object? sender, CommandEventArgs args) => End(sender, args, false);
    private void OnCancelled(object? sender, CommandEventArgs args) => End(sender, args, true);
    private void End(object? sender, CommandEventArgs args, bool failed)
    {
      lock (_sync)
      {
        if (!ReferenceEquals(sender, _document) ||
            !string.Equals(args.GlobalCommandName, CommandName, StringComparison.OrdinalIgnoreCase) ||
            !_started || !_bodyExited) return;
        if (_error != null) Completion.TrySetException(_error);
        else if (failed) Completion.TrySetException(new JsonRpcDispatchException("CIVIL3D.COMMAND_FAILED",
          "The host cancelled or failed the command after the script ran. Inspect the drawing before any retry.", operationCommitted: _committed()));
        else Completion.TrySetResult();
      }
      Detach();
    }

    internal void Detach()
    {
      var doc = Interlocked.Exchange(ref _document, null);
      if (doc == null) return;
      doc.CommandEnded -= OnEnded;
      doc.CommandCancelled -= OnCancelled;
      doc.CommandFailed -= OnCancelled;
    }
  }
}
