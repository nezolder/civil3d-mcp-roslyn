namespace Civil3DMcpPlugin;

/// <summary>
/// Bounded least-recently-used cache of compiled scripts, keyed by the full
/// script text. A 32-bit hash key could collide and run a different cached
/// script; comparing the whole text cannot.
/// </summary>
internal sealed class ScriptCache<TScript> where TScript : class
{
  private readonly int _capacity;
  private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, TScript>>> _entries =
    new(StringComparer.Ordinal);
  private readonly LinkedList<KeyValuePair<string, TScript>> _recency = new();
  private readonly object _sync = new();

  public ScriptCache(int capacity)
  {
    if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
    _capacity = capacity;
  }

  public int Count
  {
    get { lock (_sync) return _entries.Count; }
  }

  public bool TryGet(string code, out TScript? script)
  {
    lock (_sync)
    {
      if (!_entries.TryGetValue(code, out var node))
      {
        script = null;
        return false;
      }

      _recency.Remove(node);
      _recency.AddFirst(node);
      script = node.Value.Value;
      return true;
    }
  }

  public void Add(string code, TScript script)
  {
    lock (_sync)
    {
      if (_entries.TryGetValue(code, out var existing))
      {
        _recency.Remove(existing);
        _entries.Remove(code);
      }

      _entries[code] = _recency.AddFirst(new KeyValuePair<string, TScript>(code, script));
      while (_entries.Count > _capacity)
      {
        var oldest = _recency.Last!;
        _recency.RemoveLast();
        _entries.Remove(oldest.Value.Key);
      }
    }
  }

  public void Clear()
  {
    lock (_sync)
    {
      _entries.Clear();
      _recency.Clear();
    }
  }
}
