namespace Civil3DMcpPlugin;

internal static class ScriptCacheTests
{
  public static void RunAll()
  {
    var tests = new (string Name, Action Run)[]
    {
      ("script cache returns only the script stored for the exact code", ExactCodeLookup),
      ("script cache distinguishes codes whose 32-bit string hashes collide", HashCollision),
      ("script cache evicts the least recently used script at capacity", LeastRecentlyUsedEviction),
      ("script cache replaces an existing entry without growing", ReplaceExisting),
      ("script cache clear removes every entry", ClearRemovesEntries),
      ("script cache rejects a non-positive capacity", InvalidCapacity),
    };

    foreach (var (name, run) in tests)
    {
      run();
      Console.WriteLine($"PASS {name}");
    }
  }

  private static void ExactCodeLookup()
  {
    var cache = new ScriptCache<string>(4);
    cache.Add("return 1;", "one");
    Assert(cache.TryGet("return 1;", out var hit) && hit == "one", "stored code must be found");
    Assert(!cache.TryGet("return 1; ", out var miss) && miss is null,
      "a code that differs by whitespace must not reuse another script");
    Assert(!cache.TryGet("RETURN 1;", out _), "lookups must be case-sensitive");
  }

  private static void HashCollision()
  {
    // Simulate the old failure: two different codes mapping to one 32-bit
    // key. The cache must key on the full text, so both stay separate.
    var cache = new ScriptCache<string>(4);
    var (first, second) = FindStringHashCollision();
    Assert(first.GetHashCode() == second.GetHashCode() && first != second,
      "test setup must produce a real string hash collision");
    cache.Add(first, "first");
    Assert(!cache.TryGet(second, out _), "a colliding code must not return another script");
    cache.Add(second, "second");
    Assert(cache.TryGet(first, out var a) && a == "first", "first colliding code keeps its script");
    Assert(cache.TryGet(second, out var b) && b == "second", "second colliding code keeps its script");
  }

  private static void LeastRecentlyUsedEviction()
  {
    var cache = new ScriptCache<string>(2);
    cache.Add("a", "A");
    cache.Add("b", "B");
    Assert(cache.TryGet("a", out _), "a must be cached before eviction");
    cache.Add("c", "C");
    Assert(cache.Count == 2, "cache must not grow beyond capacity");
    Assert(!cache.TryGet("b", out _), "the least recently used entry must be evicted");
    Assert(cache.TryGet("a", out _) && cache.TryGet("c", out _),
      "recently used and newly added entries must remain");
  }

  private static void ReplaceExisting()
  {
    var cache = new ScriptCache<string>(2);
    cache.Add("a", "old");
    cache.Add("a", "new");
    Assert(cache.Count == 1, "replacing an entry must not add a second one");
    Assert(cache.TryGet("a", out var value) && value == "new", "replacement must win");
  }

  private static void ClearRemovesEntries()
  {
    var cache = new ScriptCache<string>(2);
    cache.Add("a", "A");
    cache.Add("b", "B");
    cache.Clear();
    Assert(cache.Count == 0 && !cache.TryGet("a", out _), "clear must remove every entry");
    cache.Add("c", "C");
    Assert(cache.TryGet("c", out _), "the cache must remain usable after clear");
  }

  private static void InvalidCapacity()
  {
    try
    {
      _ = new ScriptCache<string>(0);
    }
    catch (ArgumentOutOfRangeException)
    {
      return;
    }
    throw new InvalidOperationException("capacity 0 must be rejected");
  }

  private static (string First, string Second) FindStringHashCollision()
  {
    // String hashes are randomized per process, so search at run time. With
    // 32-bit hashes a collision is expected within roughly 2^16 candidates.
    var seen = new Dictionary<int, string>();
    for (var i = 0; i < 2_000_000; i++)
    {
      var code = $"return {i};";
      var hash = code.GetHashCode();
      if (seen.TryGetValue(hash, out var existing)) return (existing, code);
      seen[hash] = code;
    }
    throw new InvalidOperationException("no string hash collision found");
  }

  private static void Assert(bool condition, string message)
  {
    if (!condition) throw new InvalidOperationException(message);
  }
}
