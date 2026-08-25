namespace Autodesk.AutoCAD.EditorInput
{
  public sealed class Editor { }
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
    public int CommandContextCallCount => Volatile.Read(ref _commandContextCallCount);

    public async Task ExecuteInCommandContextAsync(
      Func<object?, Task> callback,
      object? userData)
    {
      Interlocked.Increment(ref _commandContextCallCount);
      if (CommandContextDelay > TimeSpan.Zero)
      {
        await Task.Delay(CommandContextDelay);
      }
      await callback(userData);
    }
  }

  public static class Application
  {
    public static DocumentCollection DocumentManager { get; } = new();
    public static int DwgTitled { get; set; } = 1;

    public static object GetSystemVariable(string name)
      => name == "DWGTITLED"
        ? DwgTitled
        : throw new InvalidOperationException($"Unsupported test system variable: {name}");
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
