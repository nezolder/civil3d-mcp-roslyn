using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

/// <summary>
/// Process-local write idempotency reservations. This intentionally keeps no
/// result payload and is reset whenever the plugin process restarts.
/// </summary>
internal sealed class IdempotencyRegistry
{
  internal const int CompletedCapacity = 256;

  private readonly object _sync = new();
  private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
  private readonly LinkedList<string> _completedKeys = new();

  internal IdempotencyReservation Reserve(
    JsonObject? parameters,
    string code,
    ExpectedDrawing expectedDrawing,
    bool saveDrawing)
  {
    var key = ParseOptionalKey(parameters);
    if (key == null) return IdempotencyReservation.None;

    var binding = IdempotencyBinding.Create(code, expectedDrawing, saveDrawing);
    lock (_sync)
    {
      if (_entries.TryGetValue(key, out var existing))
      {
        if (existing.Binding != binding)
        {
          throw Error(
            "CIVIL3D.IDEMPOTENCY_CONFLICT",
            "The idempotencyKey is already bound to different code, expectedDrawing, or saveDrawing."
          );
        }

        throw existing.State == EntryState.Completed
          ? Error(
            "CIVIL3D.IDEMPOTENCY_COMPLETED",
            "A prior matching write committed successfully. Reconcile the drawing with a read-only query; no prior result is retained."
          )
          : Error(
            "CIVIL3D.IDEMPOTENCY_IN_PROGRESS",
            "A matching write with this idempotencyKey is already in progress."
          );
      }

      _entries.Add(key, new Entry(binding, EntryState.InProgress));
      return new IdempotencyReservation(this, key, binding);
    }
  }

  internal void Complete(string key, IdempotencyBinding binding)
  {
    lock (_sync)
    {
      if (!_entries.TryGetValue(key, out var entry) ||
          entry.Binding != binding ||
          entry.State != EntryState.InProgress)
      {
        throw new InvalidOperationException("Idempotency reservation was not active.");
      }

      entry.State = EntryState.Completed;
      _completedKeys.AddLast(key);
      while (_completedKeys.Count > CompletedCapacity)
      {
        var oldest = _completedKeys.First!;
        _completedKeys.RemoveFirst();
        _entries.Remove(oldest.Value);
      }
    }
  }

  internal void Release(string key, IdempotencyBinding binding)
  {
    lock (_sync)
    {
      if (_entries.TryGetValue(key, out var entry) &&
          entry.Binding == binding &&
          entry.State == EntryState.InProgress)
      {
        _entries.Remove(key);
      }
    }
  }

  internal void ResetForTests()
  {
    lock (_sync)
    {
      _entries.Clear();
      _completedKeys.Clear();
    }
  }

  private static string? ParseOptionalKey(JsonObject? parameters)
  {
    if (parameters == null || !parameters.ContainsKey("idempotencyKey")) return null;
    if (parameters["idempotencyKey"] is not JsonValue value ||
        !value.TryGetValue<string>(out var key) ||
        key == null ||
        key.Length is < 1 or > 128 ||
        key.Any(character => !IsAllowedKeyCharacter(character)))
    {
      throw Error(
        "CIVIL3D.INVALID_INPUT",
        "Parameter 'idempotencyKey' must be 1 to 128 characters using only A-Z, a-z, 0-9, '.', '_', ':', or '-'."
      );
    }

    return key;
  }

  private static bool IsAllowedKeyCharacter(char value)
    => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or ':' or '-';

  private static JsonRpcDispatchException Error(string code, string message)
    => new(code, message);

  private sealed class Entry
  {
    public Entry(IdempotencyBinding binding, EntryState state)
    {
      Binding = binding;
      State = state;
    }

    public IdempotencyBinding Binding { get; }
    public EntryState State { get; set; }
  }

  private enum EntryState
  {
    InProgress,
    Completed,
  }
}

internal readonly record struct IdempotencyBinding(
  string CodeSha256,
  string DatabaseFilename,
  Guid FingerprintGuid,
  bool SaveDrawing)
{
  public static IdempotencyBinding Create(
    string code,
    ExpectedDrawing expectedDrawing,
    bool saveDrawing)
  {
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
    return new IdempotencyBinding(
      hash,
      expectedDrawing.DatabaseFilename.ToUpperInvariant(),
      expectedDrawing.FingerprintGuid,
      saveDrawing
    );
  }
}

internal sealed class IdempotencyReservation
{
  public static readonly IdempotencyReservation None = new();

  private readonly IdempotencyRegistry? _registry;
  private readonly string? _key;
  private readonly IdempotencyBinding _binding;
  private bool _completed;

  private IdempotencyReservation()
  {
  }

  internal IdempotencyReservation(
    IdempotencyRegistry registry,
    string key,
    IdempotencyBinding binding)
  {
    _registry = registry;
    _key = key;
    _binding = binding;
  }

  public void Complete()
  {
    if (_registry == null || _key == null) return;
    _registry.Complete(_key, _binding);
    _completed = true;
  }

  public void ReleaseIfIncomplete()
  {
    if (!_completed && _registry != null && _key != null)
    {
      _registry.Release(_key, _binding);
    }
  }
}
