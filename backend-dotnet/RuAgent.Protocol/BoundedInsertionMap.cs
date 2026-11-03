namespace RuAgent.Protocol;

/// <summary>
/// A small insertion-ordered map with a hard capacity: once full, adding a new
/// key evicts the oldest inserted one.
/// </summary>
/// <remarks>
/// A plain <see cref="Dictionary{TKey,TValue}"/> cannot express this. Its
/// enumeration order is an implementation detail, and a freed slot is reused by
/// the next insertion, so <c>Remove(dictionary.Keys.First())</c> evicts an
/// arbitrary entry — commonly the row that was just added. Callers that evicted
/// that way (bounded tool rows and park reasons in the transcript) lost the
/// newest entries first as soon as one run produced more than the cap.
///
/// The map is not thread-safe; callers already serialize their use (the CLI
/// transcript runs on its UI thread and the background-task tracker holds a
/// lock). Updating an existing key keeps its original position, matching the
/// dictionary semantics the call sites replaced.
/// </remarks>
public sealed class BoundedInsertionMap<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _items;
    private readonly int _capacity;
    private long _nextOrder;

    public BoundedInsertionMap(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _items = new Dictionary<TKey, Entry>(capacity, comparer);
    }

    public int Count => _items.Count;

    public int Capacity => _capacity;

    public TValue this[TKey key]
    {
        get => _items[key].Value;
        set
        {
            if (_items.TryGetValue(key, out var existing))
            {
                _items[key] = existing with { Value = value };
                return;
            }

            _items[key] = new Entry(_nextOrder++, value);
            while (_items.Count > _capacity)
            {
                EvictOldest();
            }
        }
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_items.TryGetValue(key, out var entry))
        {
            value = entry.Value;
            return true;
        }

        value = default!;
        return false;
    }

    public bool ContainsKey(TKey key) => _items.ContainsKey(key);

    public bool Remove(TKey key) => _items.Remove(key);

    /// <summary>A snapshot, so a caller may remove entries while enumerating it.</summary>
    public IReadOnlyList<TKey> Keys
    {
        get
        {
            var keys = new TKey[_items.Count];
            var index = 0;
            foreach (var pair in _items.OrderBy(pair => pair.Value.Order))
            {
                keys[index++] = pair.Key;
            }

            return keys;
        }
    }

    public void Clear() => _items.Clear();

    private void EvictOldest()
    {
        var oldestKey = default(TKey);
        var oldestOrder = long.MaxValue;
        var found = false;
        foreach (var pair in _items)
        {
            if (pair.Value.Order < oldestOrder)
            {
                oldestOrder = pair.Value.Order;
                oldestKey = pair.Key;
                found = true;
            }
        }

        if (found)
        {
            _items.Remove(oldestKey!);
        }
    }

    private readonly record struct Entry(long Order, TValue Value);
}
