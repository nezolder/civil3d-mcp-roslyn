using System.Collections.Concurrent;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

// Enters a real modal command instead of awaiting Autodesk's ExecutionResult.
// The existing operation gate remains held until this request's host command ends.
internal static class ModalCommandAdmission
{
  // Writes run through this command and leave one normal Undo step each.
  internal const string CommandName = "C3DMCPRUN";
  // Reads run through this one, which is registered without an Undo marker,
  // so that a query does not stand between the user's Ctrl+Z and their work.
  internal const string ReadCommandName = "C3DMCPREAD";
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

  internal static async Task ExecuteAsync(
    Action body, CancellationToken cancellationToken, Func<bool> committed, bool readOnly = false)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var request = new Request(body, committed, readOnly ? ReadCommandName : CommandName);
    Requests[request.Token] = request;
    using var deadline = new CancellationTokenSource(StartDeadlineOverrideForTests ?? TimeSpan.FromSeconds(15));
    using var registration = cancellationToken.Register(() =>
      request.Abandon(new OperationCanceledException(cancellationToken)));
    using var deadlineRegistration = deadline.Token.Register(() =>
      request.Abandon(new JsonRpcDispatchException("CIVIL3D.COMMAND_CONTEXT_TIMEOUT",
        "The modal command did not start within 15 seconds; the script did not run. " + request.DescribeWait())));
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
      if (!doc.Editor.IsQuiescent) { request.NoteBusyHost(); continue; }
      try { request.Queue(doc); }
      catch (Exception ex) { request.Abandon(ex); }
    }
  }

  // Called only by a genuine modal CommandMethod, never by the async context API.
  internal static void RunCommand() => RunCommand(CommandName);
  internal static void RunReadCommand() => RunCommand(ReadCommandName);

  private static void RunCommand(string commandName)
  {
    var doc = App.DocumentManager.MdiActiveDocument;
    if (doc == null) return;
    var token = doc.Editor.GetString(new PromptStringOptions("\nMCP request: ") { AllowSpaces = false });
    if (token.Status != PromptStatus.OK || !Requests.TryGetValue(token.StringResult, out var request)) return;
    // A request runs only under the command it was queued for: a write must
    // never run inside the command that leaves no Undo step.
    if (!string.Equals(request.CommandName, commandName, StringComparison.Ordinal)) return;
    request.Run(doc);
  }

  /// <summary>
  /// The manual way out of a stuck operation, for the person at the computer
  /// (command C3DMCPRESET). A command typed in Civil 3D can only run while no
  /// script body is running, so what it finds is bookkeeping: a request that
  /// never started, or one whose body finished but whose command end the host
  /// never reported. Both are completed with an error and the gate is freed.
  /// </summary>
  internal static OperatorReleaseReport ReleaseByOperator()
  {
    int notStarted = 0, finished = 0, running = 0;
    foreach (var request in Requests.Values)
    {
      switch (request.ReleaseByOperator())
      {
        case OperatorRelease.NotStarted: notStarted++; break;
        case OperatorRelease.Finished: finished++; break;
        case OperatorRelease.StillRunning: running++; break;
      }
    }
    return new OperatorReleaseReport(notStarted, finished, running);
  }

  internal sealed record OperatorReleaseReport(int NotStarted, int Finished, int StillRunning);
  private enum OperatorRelease { Nothing, NotStarted, Finished, StillRunning }

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
    private bool _hostWasBusy;
    internal string Token { get; } = Guid.NewGuid().ToString("N");
    internal string CommandName { get; }
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Request(Action body, Func<bool> committed, string commandName)
    {
      _body = body;
      _committed = committed;
      CommandName = commandName;
    }
    internal bool CanQueue { get { lock (_sync) return !_queued && !_abandoned; } }
    internal void NoteBusyHost() { lock (_sync) _hostWasBusy = true; }

    /// <summary>Why the command has not started yet, as far as this side can tell.</summary>
    internal string DescribeWait()
    {
      lock (_sync)
      {
        if (_queued)
        {
          return "The command was handed to Civil 3D but did not begin: another command or prompt may be holding its command line.";
        }
        // Idle moments were seen, but a command or prompt held the command line.
        if (_hostWasBusy)
        {
          return "A command or prompt was waiting for input in Civil 3D: finish it there, then try again.";
        }
        // No idle moment at all. A running command gives none either (seen
        // live with DELAY), so this side cannot tell the two cases apart.
        return "Civil 3D did not become idle in that time. If a command is running there, let it finish; " +
          "if nothing is running, click once into its window. Then try again.";
      }
    }

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

    internal OperatorRelease ReleaseByOperator()
    {
      lock (_sync)
      {
        if (_abandoned) return OperatorRelease.Nothing;
        if (!_started)
        {
          _abandoned = true;
          _body = null;
          return Completion.TrySetException(new JsonRpcDispatchException("CIVIL3D.COMMAND_CONTEXT_TIMEOUT",
            "The waiting request was released in Civil 3D with C3DMCPRESET before it started; the script did not run."))
            ? OperatorRelease.NotStarted
            : OperatorRelease.Nothing;
        }
        if (!_bodyExited) return OperatorRelease.StillRunning;
        var error = _error ?? new JsonRpcDispatchException("CIVIL3D.COMMAND_FAILED",
          "The script ran, but Civil 3D never reported the end of its command, and the operation was released by hand " +
            "with C3DMCPRESET. Inspect the drawing before any retry.", operationCommitted: _committed());
        if (!Completion.TrySetException(error)) return OperatorRelease.Nothing;
      }
      Detach();
      return OperatorRelease.Finished;
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
