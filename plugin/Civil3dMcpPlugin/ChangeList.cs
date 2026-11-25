using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

internal enum ChangeKind
{
  Added,
  Modified,
  Erased,
}

/// <summary>One drawing object a write created, changed or erased.</summary>
internal sealed record ChangedObject(string Type, string? Handle, string? Name = null, string? Layer = null);

/// <summary>
/// What one write did to the drawing, as plain data: how many objects of
/// which type were added, modified and erased, and the first few of each by
/// handle and name. It is reported in the receipt next to the script result,
/// so the caller does not have to trust the script's own account of its work.
/// This class holds no Autodesk object; <see cref="DrawingChangeTracker"/>
/// fills it.
/// </summary>
internal sealed class ChangeList
{
  internal const int DefaultItemLimit = 10;
  internal const int MaxItemLimit = 100;
  private const int MaxTextLength = 80;
  private const int MaxTypesPerGroup = 40;

  private readonly int _itemLimit;
  private readonly Group _added = new();
  private readonly Group _modified = new();
  private readonly Group _erased = new();
  private readonly Dictionary<string, int> _containers = new(StringComparer.Ordinal);

  internal ChangeList(int itemLimit)
  {
    _itemLimit = Math.Clamp(itemLimit, 0, MaxItemLimit);
  }

  /// <summary>The list could not be made; the write itself is not affected.</summary>
  internal static ChangeList Unavailable(string reason)
    => new(0) { UnavailableReason = ExecutionRecord.ToSingleLine(reason, 200) ?? "unknown reason" };

  /// <summary>
  /// The script or its commit failed, so nothing was read. What can still be
  /// said is whether anything the script added is left in the drawing.
  /// </summary>
  internal static ChangeList AfterFailure(int? left, IReadOnlyDictionary<string, int>? leftByType = null)
    => new(0)
    {
      UnavailableReason = "the script failed, so its changes were not read",
      LeftAfterFailure = left,
      LeftByType = leftByType,
    };

  internal string? UnavailableReason { get; private init; }
  internal bool IsAvailable => UnavailableReason == null;

  /// <summary>The script ran as a preview: its transaction was not committed.</summary>
  internal bool Preview { get; set; }

  /// <summary>
  /// Preview only. Objects that still exist, or are still erased, after the
  /// transaction was abandoned; anything but zero means the drawing was changed.
  /// Null when the check has not run.
  /// </summary>
  internal int? LeftAfterPreview { get; set; }

  /// <summary>
  /// After a failed script: objects it added that still exist. Null when the
  /// check did not run or does not apply.
  /// </summary>
  internal int? LeftAfterFailure { get; private init; }

  /// <summary>
  /// What the check after a preview or a failure found left behind, by type,
  /// so that a nonzero count can be told apart from bookkeeping objects.
  /// </summary>
  internal IReadOnlyDictionary<string, int>? LeftByType { get; set; }

  /// <summary>
  /// True when nothing was kept and the check after the abandoned transaction
  /// found nothing left: the drawing is as it was before the request.
  /// </summary>
  internal bool NothingLeft => (Preview ? LeftAfterPreview : LeftAfterFailure) == 0;

  /// <summary>More new handles were given out than the scan looks at.</summary>
  internal bool AddedScanTruncated { get; set; }

  /// <summary>More objects were touched than the tracker remembers.</summary>
  internal bool TouchedOverflow { get; set; }

  /// <summary>
  /// Changes Civil 3D makes on its own, outside the script's transaction, were
  /// not observed (database events switched off), so the modified and erased
  /// groups can miss some. A preview never sees what a commit would trigger.
  /// </summary>
  internal bool InternalChangesUnobserved { get; set; }

  internal int AddedCount => _added.Count;
  internal int ModifiedCount => _modified.Count;
  internal int ErasedCount => _erased.Count;

  /// <summary>True while another object of this kind would still be listed by name.</summary>
  internal bool WantsItem(ChangeKind kind) => GroupOf(kind).Items.Count < _itemLimit;

  internal void Add(ChangeKind kind, ChangedObject changed)
  {
    var group = GroupOf(kind);
    group.Count++;
    group.ByType[changed.Type] = group.ByType.TryGetValue(changed.Type, out var count) ? count + 1 : 1;
    if (group.Items.Count < _itemLimit) group.Items.Add(changed);
  }

  /// <summary>
  /// Tables, dictionaries and layout blocks change whenever something is put
  /// into them. They are counted apart so that they do not hide the objects
  /// the write was about.
  /// </summary>
  internal void AddContainer(string type)
    => _containers[type] = _containers.TryGetValue(type, out var count) ? count + 1 : 1;

  private Group GroupOf(ChangeKind kind) => kind switch
  {
    ChangeKind.Added => _added,
    ChangeKind.Modified => _modified,
    _ => _erased,
  };

  internal JsonObject ToJson()
  {
    if (!IsAvailable)
    {
      var unavailable = new JsonObject
      {
        ["available"] = false,
        ["reason"] = UnavailableReason,
      };
      if (LeftAfterFailure is int leftBehind)
      {
        unavailable["rolledBack"] = leftBehind == 0;
        if (leftBehind != 0)
        {
          unavailable["leftBehind"] = leftBehind;
          AddLeftTypes(unavailable);
        }
      }
      return unavailable;
    }

    var json = new JsonObject
    {
      ["added"] = ToJson(_added),
      ["modified"] = ToJson(_modified),
      ["erased"] = ToJson(_erased),
    };
    if (_containers.Count > 0) json["containers"] = ToJson(_containers);
    if (Preview && LeftAfterPreview is int left)
    {
      json["rolledBack"] = left == 0;
      if (left != 0)
      {
        json["leftAfterPreview"] = left;
        AddLeftTypes(json);
      }
    }

    var complete = !Preview && !AddedScanTruncated && !TouchedOverflow && !InternalChangesUnobserved;
    json["complete"] = complete;
    if (!complete) json["note"] = DescribeGaps();
    return json;
  }

  private void AddLeftTypes(JsonObject json)
  {
    if (LeftByType is { Count: > 0 } types) json["leftTypes"] = ToJson(new Dictionary<string, int>(types, StringComparer.Ordinal));
  }

  private string DescribeGaps()
  {
    var gaps = new List<string>();
    if (Preview)
    {
      gaps.Add("a preview lists what the script itself did; what Civil 3D would update on its own " +
        "when the change is kept (dependent objects, rebuilds) is not included");
    }
    else if (InternalChangesUnobserved)
    {
      gaps.Add("modified and erased list only objects the script opened in its transaction; " +
        "objects Civil 3D changes on its own (for example on a rebuild) can be missing");
    }
    if (AddedScanTruncated) gaps.Add("more objects were added than the scan looks at, so added is a lower bound");
    if (TouchedOverflow) gaps.Add("more objects were touched than are remembered, so modified and erased are lower bounds");
    return string.Join("; ", gaps) + ".";
  }

  private static JsonObject ToJson(Group group)
  {
    var json = new JsonObject { ["count"] = group.Count };
    if (group.Count == 0) return json;
    json["byType"] = ToJson(group.ByType);
    if (group.Items.Count == 0) return json;
    var items = new JsonArray();
    foreach (var item in group.Items)
    {
      var entry = new JsonObject { ["type"] = item.Type };
      if (!string.IsNullOrEmpty(item.Handle)) entry["handle"] = item.Handle;
      var name = ExecutionRecord.ToSingleLine(item.Name, MaxTextLength);
      if (name != null) entry["name"] = name;
      var layer = ExecutionRecord.ToSingleLine(item.Layer, MaxTextLength);
      if (layer != null) entry["layer"] = layer;
      items.Add(entry);
    }
    json["items"] = items;
    if (group.Count > group.Items.Count) json["more"] = group.Count - group.Items.Count;
    return json;
  }

  /// <summary>Most frequent first; a very long tail is summed up under "other".</summary>
  private static JsonObject ToJson(Dictionary<string, int> counts)
  {
    var json = new JsonObject();
    var ordered = counts
      .OrderByDescending(pair => pair.Value)
      .ThenBy(pair => pair.Key, StringComparer.Ordinal)
      .ToList();
    foreach (var pair in ordered.Take(MaxTypesPerGroup)) json[pair.Key] = pair.Value;
    if (ordered.Count > MaxTypesPerGroup) json["other"] = ordered.Skip(MaxTypesPerGroup).Sum(pair => pair.Value);
    return json;
  }

  private sealed class Group
  {
    internal int Count;
    internal readonly Dictionary<string, int> ByType = new(StringComparer.Ordinal);
    internal readonly List<ChangedObject> Items = new();
  }
}
