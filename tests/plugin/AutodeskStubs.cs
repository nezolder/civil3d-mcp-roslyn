namespace Autodesk.AutoCAD.EditorInput
{
  public enum PromptStatus { OK, Cancel }
  public sealed class PromptStringOptions(string message)
  {
    public string Message { get; } = message;
    public bool AllowSpaces { get; set; }
  }
  public sealed record PromptResult(PromptStatus Status, string StringResult);
  public sealed class Editor
  {
    public bool IsQuiescent { get; set; } = true;
    public string CommandToken { get; set; } = string.Empty;
    public PromptResult GetString(PromptStringOptions options) => new(PromptStatus.OK, CommandToken);
    public void WriteMessage(string message) { }
  }
}

namespace Autodesk.AutoCAD.DatabaseServices
{
  public enum DwgVersion
  {
    Current,
  }

  public sealed class SecurityParameters { }

  public readonly struct Handle
  {
    public Handle(long value)
    {
      Value = value;
    }

    public long Value { get; }
    public override string ToString() => Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
  }

  public readonly struct ObjectId
  {
    private readonly bool _hasValue;
    private readonly Handle _handle;

    public ObjectId(Handle handle)
    {
      _hasValue = true;
      _handle = handle;
    }

    public static ObjectId Null => default;
    public bool IsNull => !_hasValue;
    public bool IsValid => _hasValue;
    public Handle Handle => IsValid
      ? _handle
      : throw new InvalidOperationException("Null ObjectId has no handle.");
  }

  public class DBObject { }

  public sealed class Database
  {
    private int _saveAsCallCount;

    public string Filename { get; set; } = string.Empty;
    public string FingerprintGuid { get; set; } = string.Empty;
    public TransactionManager TransactionManager { get; } = new();
    public DwgVersion OriginalFileVersion { get; set; } = DwgVersion.Current;
    public SecurityParameters SecurityParameters { get; } = new();
    public int SaveAsCallCount => Volatile.Read(ref _saveAsCallCount);
    public string? LastSavedFilename { get; private set; }
    public Action? BeforeSaveAs { get; set; }
    public Exception? SaveAsException { get; set; }

    public void SaveAs(
      string fileName,
      bool createBackupAndRename,
      DwgVersion version,
      SecurityParameters securityParameters)
    {
      BeforeSaveAs?.Invoke();
      Interlocked.Increment(ref _saveAsCallCount);
      LastSavedFilename = fileName;
      if (SaveAsException != null) throw SaveAsException;
    }
  }

  public sealed class TransactionManager
  {
    private int _activeCount;
    private int _committedCount;
    private int _startedCount;

    public int ActiveCount => Volatile.Read(ref _activeCount);
    public int CommittedCount => Volatile.Read(ref _committedCount);
    public int StartedCount => Volatile.Read(ref _startedCount);
    public Exception? CommitException { get; set; }

    public Transaction StartTransaction()
    {
      Interlocked.Increment(ref _startedCount);
      Interlocked.Increment(ref _activeCount);
      return new Transaction(this);
    }

    internal void RecordCommit() => Interlocked.Increment(ref _committedCount);
    internal void RecordDispose() => Interlocked.Decrement(ref _activeCount);
  }

  public sealed class Transaction : IDisposable
  {
    private TransactionManager? _owner;
    private bool _committed;

    internal Transaction(TransactionManager owner)
    {
      _owner = owner;
    }

    public void Commit()
    {
      if (_committed) return;
      if (_owner!.CommitException != null) throw _owner.CommitException;
      _committed = true;
      _owner!.RecordCommit();
    }

    public void Dispose()
    {
      Interlocked.Exchange(ref _owner, null)?.RecordDispose();
    }
  }
}

namespace Autodesk.AutoCAD.Geometry
{
  public readonly record struct Point2d(double X, double Y);
  public readonly record struct Point3d(double X, double Y, double Z);
  public readonly record struct Vector2d(double X, double Y);
  public readonly record struct Vector3d(double X, double Y, double Z);
}

namespace Autodesk.AutoCAD.ApplicationServices
{
  using Autodesk.AutoCAD.DatabaseServices;
  using Autodesk.AutoCAD.EditorInput;

  public sealed class TestAutodeskNamespaceSynchronizationContext : SynchronizationContext { }

  public sealed class CommandEventArgs(string name) : EventArgs { public string GlobalCommandName => name; }

  public sealed class Document
  {
    private int _activeLockCount;
    private int _lockCount;

    public Document(Database database)
    {
      Database = database;
    }

    public Database Database { get; }
    public Editor Editor { get; } = new();
    public string Name { get; set; } = "TestDrawing";
    public int ActiveLockCount => Volatile.Read(ref _activeLockCount);
    public int LockCount => Volatile.Read(ref _lockCount);
    public event EventHandler<CommandEventArgs>? CommandEnded;
    public event EventHandler<CommandEventArgs>? CommandCancelled;
    public event EventHandler<CommandEventArgs>? CommandFailed;
    public int CommandHandlerCount => (CommandEnded?.GetInvocationList().Length ?? 0)
      + (CommandCancelled?.GetInvocationList().Length ?? 0) + (CommandFailed?.GetInvocationList().Length ?? 0);
    public Action<Document, string>? CommandQueued { get; set; }
    public void SendStringToExecute(string command, bool activate, bool wrapUpInactiveDoc, bool echo)
    {
      Editor.CommandToken = command.Trim().Split(' ')[1];
      CommandQueued?.Invoke(this, Editor.CommandToken);
    }
    public void EndCommand(string name) => CommandEnded?.Invoke(this, new(name));
    public void CancelCommand(string name) => CommandCancelled?.Invoke(this, new(name));
    public void FailCommand(string name) => CommandFailed?.Invoke(this, new(name));

    public IDisposable LockDocument()
    {
      Interlocked.Increment(ref _lockCount);
      Interlocked.Increment(ref _activeLockCount);
      return new DocumentLock(this);
    }

    private sealed class DocumentLock : IDisposable
    {
      private Document? _owner;

      public DocumentLock(Document owner)
      {
        _owner = owner;
      }

      public void Dispose()
      {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner != null) Interlocked.Decrement(ref owner._activeLockCount);
      }
    }
  }

  public sealed class DocumentCollection
  {
    private int _commandContextCallCount;

    public Document? MdiActiveDocument { get; set; }
    public TimeSpan CommandContextDelay { get; set; }
    public Exception? CommandContextScheduleException { get; set; }
    public Exception? CommandContextCompletionException { get; set; }
    public Func<Func<object?, Task>, object?, Task>? CommandContextScheduleOverrideAsync { get; set; }
    public bool CompleteDuringOnCompletedRegistration { get; set; }
    /// <summary>
    /// Host-free test hook that models a native awaiter which becomes complete
    /// but never invokes the continuation registered by managed code.
    /// </summary>
    public bool SuppressCompletionContinuation { get; set; }
    /// <summary>
    /// Host-free test hook invoked after command-context dispatch is requested
    /// but before AutoCAD enters the supplied callback.
    /// </summary>
    public Func<Task>? BeforeCommandContextAsync { get; set; }

    /// <summary>
    /// Host-free test hook invoked after the supplied callback has completed,
    /// while the outer AutoCAD command-context task is still pending.
    /// </summary>
    public Func<Task>? AfterCommandContextAsync { get; set; }

    public int CommandContextCallCount => Volatile.Read(ref _commandContextCallCount);
    public Task? LastCallbackTask { get; private set; }
    private ExecutionResult? LastExecutionResult { get; set; }

    /// <summary>
    /// Explicitly releases a continuation that was intentionally suppressed by
    /// <see cref="SuppressCompletionContinuation"/>. Tests use this only to
    /// clean up a deliberately pending operation.
    /// </summary>
    public bool DeliverSuppressedCompletionContinuation()
      => LastExecutionResult?.DeliverCapturedContinuation() ?? false;

    public ExecutionResult ExecuteInCommandContextAsync(
      Func<object?, Task> callback,
      object? userData)
    {
      var result = new ExecutionResult
      {
        SuppressCompletionContinuation = SuppressCompletionContinuation,
      };
      LastExecutionResult = result;
      if (CompleteDuringOnCompletedRegistration)
      {
        result.CompleteDuringOnCompletedRegistration = () =>
          result.CompleteFromTask(StartScheduledTask(callback, userData));
      }
      else
      {
        result.CompleteFromTask(StartScheduledTask(callback, userData));
      }
      return result;
    }

    private Task StartScheduledTask(Func<object?, Task> callback, object? userData)
    {
      try
      {
        var scheduleOverride = CommandContextScheduleOverrideAsync;
        return scheduleOverride != null
          ? scheduleOverride(callback, userData)
          : ExecuteDefaultAsync(callback, userData);
      }
      catch (Exception ex)
      {
        return Task.FromException(ex);
      }
    }

    private async Task ExecuteDefaultAsync(
      Func<object?, Task> callback,
      object? userData)
    {
      if (CommandContextScheduleException != null)
      {
        throw CommandContextScheduleException;
      }
      Interlocked.Increment(ref _commandContextCallCount);
      var before = BeforeCommandContextAsync;
      if (before != null)
      {
        await before();
      }
      if (CommandContextDelay > TimeSpan.Zero)
      {
        await Task.Delay(CommandContextDelay);
      }
      var callbackTask = callback(userData);
      LastCallbackTask = callbackTask;
      await callbackTask;
      var after = AfterCommandContextAsync;
      if (after != null)
      {
        await after();
      }
      if (CommandContextCompletionException != null)
      {
        throw CommandContextCompletionException;
      }
    }

    public sealed class ExecutionResult : System.Runtime.CompilerServices.INotifyCompletion
    {
      private int _completed;
      private Action? _continuation;
      private Exception? _exception;

      internal Action? CompleteDuringOnCompletedRegistration { get; set; }
      internal bool SuppressCompletionContinuation { get; set; }
      public bool IsCompleted => Volatile.Read(ref _completed) != 0;
      public ExecutionResult GetAwaiter() => this;

      public void OnCompleted(Action continuation)
      {
        var completeDuringRegistration = CompleteDuringOnCompletedRegistration;
        CompleteDuringOnCompletedRegistration = null;
        completeDuringRegistration?.Invoke();
        _continuation = continuation;
      }

      public void GetResult()
      {
        if (_exception != null) throw _exception;
      }

      [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
      public static void Continuation()
        => throw new InvalidOperationException("firstchance-private-marker");

      internal void CompleteFromTask(Task task)
      {
        if (task.IsCompleted)
        {
          try
          {
            task.GetAwaiter().GetResult();
            Complete(null);
          }
          catch (Exception ex)
          {
            Complete(ex);
          }
          return;
        }
        _ = CompleteFromTaskAsync(task);
      }

      private async Task CompleteFromTaskAsync(Task task)
      {
        try
        {
          await task;
          Complete(null);
        }
        catch (Exception ex)
        {
          Complete(ex);
        }
      }

      private void Complete(Exception? exception)
      {
        _exception = exception;
        Volatile.Write(ref _completed, 1);
        if (!SuppressCompletionContinuation)
        {
          DeliverCapturedContinuation();
        }
      }

      internal bool DeliverCapturedContinuation()
      {
        var continuation = Interlocked.Exchange(ref _continuation, null);
        if (continuation == null) return false;
        continuation();
        return true;
      }
    }
  }

  public static class Application
  {
    public static event EventHandler? Idle;
    public static void RaiseIdle() => Idle?.Invoke(null, EventArgs.Empty);
    public static DocumentCollection DocumentManager { get; } = new();
    public static int DwgTitled { get; set; } = 1;

    public static object GetSystemVariable(string name)
      => name == "DWGTITLED"
        ? DwgTitled
        : throw new InvalidOperationException($"Unsupported test system variable: {name}");
  }
}

namespace Autodesk.AutoCAD.Runtime
{
  [Flags]
  public enum CommandFlags
  {
    Modal = 0, Transparent = 1, UsePickSet = 2, Redraw = 4,
    Session = 0x200000, NoHistory = 0x800000, NoUndoMarker = 0x1000000,
  }
  [AttributeUsage(AttributeTargets.Method)]
  public sealed class CommandMethodAttribute(string name, CommandFlags flags = CommandFlags.Modal) : Attribute
  {
    public string GlobalName { get; } = name;
    public CommandFlags Flags { get; } = flags;
  }
  [AttributeUsage(AttributeTargets.Assembly)]
  public sealed class ExtensionApplicationAttribute(Type type) : Attribute { public Type Type { get; } = type; }
  [AttributeUsage(AttributeTargets.Assembly)]
  public sealed class CommandClassAttribute(Type type) : Attribute { public Type Type { get; } = type; }
  public interface IExtensionApplication { void Initialize(); void Terminate(); }

  public sealed class SynchronizationContext : global::System.Threading.SynchronizationContext
  {
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public override void Post(SendOrPostCallback d, object? state)
      => Autodesk.AutoCAD.ApplicationServices.DocumentCollection.ExecutionResult.Continuation();
  }
}

namespace Autodesk.Civil.ApplicationServices
{
  public sealed class CivilDocument { }

  public static class CivilApplication
  {
    public static CivilDocument? ActiveDocument { get; set; }
  }
}
