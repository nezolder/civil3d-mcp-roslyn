using System.Collections.Concurrent;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcDbObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace Civil3DMcpPlugin;

/// <summary>
/// Watches one write and tells what it did to the drawing.
///
/// Three sources are combined, because none of them sees everything:
///  - added: every handle the drawing gave out while the script ran (the
///    handle seed before and after). This also finds objects Civil 3D creates
///    on its own, for example the parts of a new surface.
///  - modified and erased, from the transaction: objects the script opened
///    for write whose modified or erased flag is set. These flags are the only
///    source in a preview, where nothing is committed.
///  - modified and erased, from database events: they arrive when an object
///    is closed, which for transaction objects is the commit. They also show
///    what Civil 3D updates on its own during the commit.
///
/// What an object finally counts as is read from its state, not from which
/// event was seen. Nothing here may fail the write: every step is guarded and
/// a failure only leaves the list unavailable. Main thread only.
///
/// The before/after handle seed and the transaction flags follow the approach
/// of ctrlxbruce/Autocad-Civil3D-MCP (MIT), civil3d-plugin/Util/ChangeLog.cs
/// at 8673c21; the code is written for this plugin.
/// </summary>
internal sealed class DrawingChangeTracker : IDisposable
{
  /// <summary>Set to 0 in Civil 3D's environment to leave the database events out.</summary>
  internal const string EventsEnvironmentVariable = "CIVIL3D_MCP_CHANGE_EVENTS";

  private const long MaxHandleScan = 2_000_000;
  private const int MaxTouched = 200_000;
  private const int MaxLayoutChecks = 64;

  private static readonly ConcurrentDictionary<Type, PropertyInfo?> NameProperties = new();
  private static readonly HashSet<string> GenericWrapperNames = new(StringComparer.Ordinal)
  {
    "DBObject", "Entity", "Curve", "ImpEntity", "ImpDBObject", "ImpCurve", "ProxyEntity", "ProxyObject",
  };

  private readonly Database _database;
  private readonly IntPtr _databasePointer;
  private readonly int _itemLimit;
  private readonly long _firstNewHandle;
  private readonly bool _useEvents;
  private readonly HashSet<ObjectId> _touched = new();
  private readonly HashSet<ObjectId> _erasedInTransaction = new();
  private bool _touchedOverflow;
  private bool _subscribed;
  private ChangeList? _previewList;
  private long _previewScanEnd;
  private string? _failure;

  private DrawingChangeTracker(Database database, int itemLimit, long firstNewHandle, bool useEvents)
  {
    _database = database;
    _databasePointer = database.UnmanagedObject;
    _itemLimit = itemLimit;
    _firstNewHandle = firstNewHandle;
    _useEvents = useEvents;
  }

  /// <summary>Call before the script's transaction starts. Null when tracking cannot start.</summary>
  internal static DrawingChangeTracker? Start(Database database, int itemLimit)
  {
    try
    {
      var useEvents = !string.Equals(
        Environment.GetEnvironmentVariable(EventsEnvironmentVariable), "0", StringComparison.Ordinal);
      var tracker = new DrawingChangeTracker(database, itemLimit, database.Handseed.Value, useEvents);
      if (useEvents) tracker.Subscribe();
      return tracker;
    }
    catch (System.Exception)
    {
      return null;
    }
  }

  /// <summary>
  /// Call after the script ran, while its transaction is still open. Reads the
  /// transaction's own flags; for a preview it also describes everything now,
  /// because the new objects are gone once the transaction is abandoned.
  /// </summary>
  internal void BeforeTransactionEnds(Transaction transaction, bool preview)
  {
    try
    {
      foreach (AcDbObject opened in _database.TransactionManager.GetAllObjects())
      {
        try
        {
          // Only an object open for write can have been changed here; one
          // opened for read may carry an erased flag from long before.
          if (opened == null || opened.IsDisposed || !opened.IsWriteEnabled) continue;
          if (opened.IsErased)
          {
            _erasedInTransaction.Add(opened.ObjectId);
            Touch(opened);
          }
          else if (opened.IsModified)
          {
            Touch(opened);
          }
        }
        catch (System.Exception)
        {
          // One unreadable object must not hide the others.
        }
      }

      if (!preview) return;
      // Abandoning the transaction undoes the changes, which raises the same
      // events again; they are not this script's doing.
      Unsubscribe();
      _previewList = Describe(
        id => transaction.GetObject(id, OpenMode.ForRead, true, true), preview: true, out _previewScanEnd);
    }
    catch (System.Exception ex)
    {
      _failure = ex.GetType().Name + ": " + ex.Message;
    }
  }

  /// <summary>
  /// Call after the transaction was committed or abandoned. For a committed
  /// write the drawing is read again here, in a transaction of its own, so
  /// the list shows what is in the drawing now. For a preview it checks that
  /// nothing stayed behind. Null when there is nothing to report.
  /// </summary>
  internal ChangeList? Finish(bool committed, bool preview)
  {
    Unsubscribe();
    if (_failure != null) return ChangeList.Unavailable(_failure);
    try
    {
      if (preview)
      {
        if (_previewList == null) return ChangeList.Unavailable("the preview could not be described");
        var leftByType = new Dictionary<string, int>(StringComparer.Ordinal);
        _previewList.LeftAfterPreview = CountLeftNewObjects(_previewScanEnd, leftByType) + CountStillErased();
        _previewList.LeftByType = leftByType;
        return _previewList;
      }
      if (!committed) return null;

      using var reader = _database.TransactionManager.StartOpenCloseTransaction();
      var list = Describe(id => reader.GetObject(id, OpenMode.ForRead, true, true), preview: false, out _);
      reader.Commit();
      return list;
    }
    catch (System.Exception ex)
    {
      return ChangeList.Unavailable(ex.GetType().Name + ": " + ex.Message);
    }
  }

  /// <summary>
  /// Call when the script or the commit failed and the transaction is gone.
  /// There is no list then, but the receipt can still say whether anything the
  /// script added is left in the drawing.
  /// </summary>
  internal ChangeList FinishFailed()
  {
    Unsubscribe();
    try
    {
      var scanEnd = Math.Min(_database.Handseed.Value, _firstNewHandle + MaxHandleScan);
      var leftByType = new Dictionary<string, int>(StringComparer.Ordinal);
      return ChangeList.AfterFailure(CountLeftNewObjects(scanEnd, leftByType) + CountStillErased(), leftByType);
    }
    catch (System.Exception)
    {
      return ChangeList.AfterFailure(null);
    }
  }

  public void Dispose() => Unsubscribe();

  private void Subscribe()
  {
    _database.ObjectModified += OnObjectModified;
    _database.ObjectErased += OnObjectErased;
    _subscribed = true;
  }

  private void Unsubscribe()
  {
    if (!_subscribed) return;
    _subscribed = false;
    try
    {
      _database.ObjectModified -= OnObjectModified;
      _database.ObjectErased -= OnObjectErased;
    }
    catch (System.Exception)
    {
      // A database that is going away has dropped its handlers already.
    }
  }

  private void OnObjectModified(object? sender, ObjectEventArgs args) => Touch(args?.DBObject);
  private void OnObjectErased(object? sender, ObjectErasedEventArgs args) => Touch(args?.DBObject);

  /// <summary>Runs inside Civil 3D's own notifications: remember the id and nothing else.</summary>
  private void Touch(AcDbObject? changed)
  {
    try
    {
      if (changed == null || changed.IsDisposed) return;
      var id = changed.ObjectId;
      if (id.IsNull) return;
      if (_touched.Count >= MaxTouched)
      {
        _touchedOverflow = true;
        return;
      }
      _touched.Add(id);
    }
    catch (System.Exception)
    {
      // Never let a notification handler throw into the host.
    }
  }

  private ChangeList Describe(Func<ObjectId, AcDbObject?> open, bool preview, out long scanEnd)
  {
    var list = new ChangeList(_itemLimit)
    {
      Preview = preview,
      TouchedOverflow = _touchedOverflow,
      InternalChangesUnobserved = !_useEvents,
    };
    var typeNames = new Dictionary<string, string>(StringComparer.Ordinal);
    var layoutChecks = 0;

    var seedNow = _database.Handseed.Value;
    scanEnd = Math.Min(seedNow, _firstNewHandle + MaxHandleScan);
    if (seedNow > scanEnd) list.AddedScanTruncated = true;
    for (var value = _firstNewHandle; value < scanEnd; value++)
    {
      if (!TryGetLiveId(value, out var id)) continue;
      Add(list, ChangeKind.Added, id, open, typeNames, ref layoutChecks);
    }

    foreach (var id in _touched)
    {
      try
      {
        // An object of another drawing (an xref) can sit in the same transaction.
        if (id.IsNull || id.Database?.UnmanagedObject != _databasePointer) continue;
        // New objects are found by their handles above.
        if (id.Handle.Value >= _firstNewHandle) continue;
        var erased = _erasedInTransaction.Contains(id) || id.IsErased;
        Add(list, erased ? ChangeKind.Erased : ChangeKind.Modified, id, open, typeNames, ref layoutChecks);
      }
      catch (System.Exception)
      {
        // An id that cannot be read is left out.
      }
    }
    return list;
  }

  private bool TryGetLiveId(long handleValue, out ObjectId id)
  {
    id = ObjectId.Null;
    try
    {
      return _database.TryGetObjectId(new Handle(handleValue), out id) && !id.IsNull && !id.IsErased;
    }
    catch (System.Exception)
    {
      return false;
    }
  }

  private static void Add(
    ChangeList list,
    ChangeKind kind,
    ObjectId id,
    Func<ObjectId, AcDbObject?> open,
    Dictionary<string, string> typeNames,
    ref int layoutChecks)
  {
    RXClass? objectClass = null;
    try { objectClass = id.ObjectClass; } catch (System.Exception) { }
    var className = objectClass?.Name ?? "?";

    var isBlockRecord = IsKindOf(objectClass, BlockTableRecordClass);
    var checkLayout = kind == ChangeKind.Modified && isBlockRecord && layoutChecks < MaxLayoutChecks;
    if (checkLayout) layoutChecks++;
    var wantsItem = list.WantsItem(kind);
    // Opening is the costly step, so it is done only for the listed items,
    // once per class to learn its .NET name, and to tell a layout block apart.
    AcDbObject? opened = wantsItem || checkLayout || !typeNames.ContainsKey(className)
      ? TryOpen(open, id)
      : null;

    if (!typeNames.TryGetValue(className, out var type))
    {
      type = DisplayType(opened, className);
      if (opened != null) typeNames[className] = type;
    }

    if (IsKindOf(objectClass, SymbolTableClass)
      || IsKindOf(objectClass, DictionaryClass)
      || IsKindOf(objectClass, DrawOrderTableClass)
      || (checkLayout && opened is BlockTableRecord { IsLayout: true }))
    {
      list.AddContainer(type);
      return;
    }

    string? handle = null;
    try { handle = id.Handle.ToString(); } catch (System.Exception) { }
    list.Add(kind, wantsItem && opened != null
      ? new ChangedObject(type, handle, ReadName(opened), ReadLayer(opened))
      : new ChangedObject(type, handle));
  }

  private static AcDbObject? TryOpen(Func<ObjectId, AcDbObject?> open, ObjectId id)
  {
    try
    {
      return open(id);
    }
    catch (System.Exception)
    {
      // Still open for write elsewhere, or of a kind that cannot be opened.
      return null;
    }
  }

  /// <summary>The .NET type name scripts use; the drawing class name where the wrapper is a generic one.</summary>
  private static string DisplayType(AcDbObject? opened, string className)
  {
    if (opened == null) return className;
    var name = opened.GetType().Name;
    return GenericWrapperNames.Contains(name) ? className : name;
  }

  private static string? ReadLayer(AcDbObject opened)
  {
    try
    {
      return opened is AcEntity entity ? entity.Layer : null;
    }
    catch (System.Exception)
    {
      return null;
    }
  }

  private static string? ReadName(AcDbObject opened)
  {
    try
    {
      if (opened is SymbolTableRecord record) return record.Name;
      var property = NameProperties.GetOrAdd(opened.GetType(), FindNameProperty);
      return property?.GetValue(opened) as string;
    }
    catch (System.Exception)
    {
      // Some Civil 3D objects refuse to give their name in some states.
      return null;
    }
  }

  private static PropertyInfo? FindNameProperty(Type type)
  {
    try
    {
      // The most derived readable string property called Name, if any.
      for (var current = type; current != null && current != typeof(object); current = current.BaseType)
      {
        var property = current.GetProperty(
          "Name", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (property != null
          && property.PropertyType == typeof(string)
          && property.CanRead
          && property.GetIndexParameters().Length == 0)
        {
          return property;
        }
      }
    }
    catch (System.Exception)
    {
      // No name for this type.
    }
    return null;
  }

  /// <summary>
  /// After a preview or a failure nothing the script added may still be in the
  /// drawing. A new handle whose id is not marked erased is not enough to say
  /// so: when a transaction is abandoned, an object that the drawing made by
  /// itself for a new object (the end marker of a 3D polyline, for example)
  /// can keep an id that is not marked erased although its owner is gone
  /// (seen live: one such id per abandoned 3D polyline, with the polylines
  /// themselves gone). So an object counts only when
  /// it can still be opened, is not erased, and its owner is still there and
  /// still holds it. An object that cannot be opened for another reason is
  /// counted, so that a doubt never turns into a clean mark.
  /// </summary>
  private int CountLeftNewObjects(long scanEnd, Dictionary<string, int> byType)
  {
    var left = 0;
    var verdicts = new Dictionary<ObjectId, bool>();
    var ownerContents = new Dictionary<ObjectId, HashSet<ObjectId>?>();
    using var reader = _database.TransactionManager.StartOpenCloseTransaction();
    for (var value = _firstNewHandle; value < scanEnd; value++)
    {
      if (!TryGetLiveId(value, out var id)) continue;
      if (!StillInDrawing(reader, id, verdicts, ownerContents, out var type, depth: 0)) continue;
      left++;
      byType[type] = byType.TryGetValue(type, out var count) ? count + 1 : 1;
    }
    reader.Commit();
    return left;
  }

  private bool StillInDrawing(
    Transaction reader,
    ObjectId id,
    Dictionary<ObjectId, bool> verdicts,
    Dictionary<ObjectId, HashSet<ObjectId>?> ownerContents,
    out string type,
    int depth)
  {
    type = "?";
    try { type = id.ObjectClass?.Name ?? "?"; } catch (System.Exception) { }
    if (verdicts.TryGetValue(id, out var known)) return known;

    AcDbObject opened;
    try
    {
      opened = reader.GetObject(id, OpenMode.ForRead, true, true);
    }
    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (IsGone(ex.ErrorStatus))
    {
      return Remember(verdicts, id, false);
    }
    catch (System.Exception)
    {
      return Remember(verdicts, id, true);
    }
    if (opened == null) return Remember(verdicts, id, false);
    type = DisplayType(opened, type);

    try
    {
      if (opened.IsErased) return Remember(verdicts, id, false);
      var owner = opened.OwnerId;
      if (owner.IsNull || owner.Database?.UnmanagedObject != _databasePointer) return Remember(verdicts, id, true);
      if (owner.Handle.Value >= _firstNewHandle)
      {
        // A part of something the script added: it is left only if that is.
        if (!TryGetLiveId(owner.Handle.Value, out _)) return Remember(verdicts, id, false);
        if (depth >= 16) return Remember(verdicts, id, true);
        return Remember(verdicts, id, StillInDrawing(reader, owner, verdicts, ownerContents, out _, depth + 1));
      }
      return Remember(verdicts, id, OwnerHolds(reader, owner, id, ownerContents));
    }
    catch (System.Exception)
    {
      return Remember(verdicts, id, true);
    }
  }

  private static bool Remember(Dictionary<ObjectId, bool> verdicts, ObjectId id, bool left)
  {
    verdicts[id] = left;
    return left;
  }

  /// <summary>Ids that no longer lead to an object.</summary>
  private static bool IsGone(ErrorStatus status)
    => status is ErrorStatus.WasErased or ErrorStatus.PermanentlyErased
      or ErrorStatus.NullObjectPointer or ErrorStatus.NullObjectId;

  /// <summary>
  /// Whether an object that was there before the script still lists the new
  /// object. Blocks are read once each; an owner of another kind, or one that
  /// cannot be read, is taken to hold it.
  /// </summary>
  private static bool OwnerHolds(
    Transaction reader, ObjectId owner, ObjectId id, Dictionary<ObjectId, HashSet<ObjectId>?> ownerContents)
  {
    try
    {
      if (ownerContents.TryGetValue(owner, out var known)) return known == null || known.Contains(id);
      switch (reader.GetObject(owner, OpenMode.ForRead, true, true))
      {
        case BlockTableRecord block:
          var contents = new HashSet<ObjectId>();
          foreach (ObjectId entity in block) contents.Add(entity);
          ownerContents[owner] = contents;
          return contents.Contains(id);
        case SymbolTable table:
          return table.Has(id);
        case DBDictionary dictionary:
          return dictionary.Contains(id);
        default:
          ownerContents[owner] = null;
          return true;
      }
    }
    catch (System.Exception)
    {
      return true;
    }
  }

  private int CountStillErased()
  {
    var left = 0;
    foreach (var id in _erasedInTransaction)
    {
      try
      {
        if (!id.IsNull && id.Handle.Value < _firstNewHandle && id.IsErased) left++;
      }
      catch (System.Exception)
      {
        // Unreadable ids are not counted either way.
      }
    }
    return left;
  }

  private static bool IsKindOf(RXClass? candidate, RXClass? parent)
  {
    try
    {
      return candidate != null && parent != null && candidate.IsDerivedFrom(parent);
    }
    catch (System.Exception)
    {
      return false;
    }
  }

  private static RXClass? SymbolTableClass => _symbolTableClass ??= TryGetClass(typeof(SymbolTable));
  private static RXClass? DictionaryClass => _dictionaryClass ??= TryGetClass(typeof(DBDictionary));
  private static RXClass? BlockTableRecordClass => _blockTableRecordClass ??= TryGetClass(typeof(BlockTableRecord));
  // The draw order list of a block changes with almost every entity put into
  // or taken out of it (seen live), so it is bookkeeping like the block itself.
  private static RXClass? DrawOrderTableClass => _drawOrderTableClass ??= TryGetClass(typeof(DrawOrderTable));
  private static RXClass? _symbolTableClass;
  private static RXClass? _dictionaryClass;
  private static RXClass? _blockTableRecordClass;
  private static RXClass? _drawOrderTableClass;

  private static RXClass? TryGetClass(Type type)
  {
    try
    {
      return RXObject.GetClass(type);
    }
    catch (System.Exception)
    {
      return null;
    }
  }
}
