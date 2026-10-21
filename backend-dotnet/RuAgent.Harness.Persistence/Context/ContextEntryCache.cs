using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Persistence.Context;

/// <summary>Run-source-local, bounded parsed rows. The database and current key always lead this cache.</summary>
internal sealed class ContextEntryCache
{
    internal const int MaximumEntries = 1024;
    internal const int MaximumWeight = 8 * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, LinkedListNode<Item>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Item> _order = new();
    private readonly object _gate = new();
    private int _weight;
    private string? _keyStamp;

    internal void UseKeyStamp(string stamp)
    {
        lock (_gate)
        {
            if (_keyStamp == stamp) return;
            _entries.Clear(); _order.Clear(); _weight = 0;
            _keyStamp = stamp;
        }
    }

    // Include all persisted context/receipt fields, not just payload identity.
    // A changed ciphertext, audience, source, role or receipt must take validation again.
    internal static string Fingerprint(RunId run, string keyStamp, SqliteDataReader reader)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetBytes(run.Value));
        Append(hash, Encoding.UTF8.GetBytes(keyStamp));
        for (int index = 0; index < reader.FieldCount; index++)
        {
            var value = reader.GetValue(index);
            if (value is DBNull) { hash.AppendData(BitConverter.GetBytes(-1)); continue; }
            Append(hash, value is byte[] blob ? blob
                : Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture)!));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, byte[] bytes)
    { hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes); }

    internal bool TryGet(string key, out ModelContextEntry entry)
    {
        lock (_gate)
        {
            TrimExpired();
            if (_entries.TryGetValue(key, out var node)) { entry = node.Value.Entry; return true; }
            entry = null!;
            return false;
        }
    }

    internal void Add(string key, ModelContextEntry entry, int payloadBytes)
    {
        // Conservative accounting for parsed strings/JSON; not a heap-size claim.
        var weight = checked((long)payloadBytes * 8 + 1024);
        if (weight > MaximumWeight) return;
        lock (_gate)
        {
            if (_entries.ContainsKey(key)) return;
            TrimExpired();
            // A full sequential database scan must not evict the entire cached
            // prefix before reaching it again. Retain admitted rows until expiry;
            // an over-budget suffix follows the ordinary validation path.
            if (_entries.Count >= MaximumEntries || _weight + weight > MaximumWeight) return;
            var node = _order.AddLast(new Item(key, entry, (int)weight, Stopwatch.GetTimestamp()));
            _entries.Add(key, node);
            _weight += (int)weight;
        }
    }

    internal void Clear()
    { lock (_gate) { _entries.Clear(); _order.Clear(); _weight = 0; _keyStamp = null; } }

    private void TrimExpired()
    {
        while (_order.First is { } first && Stopwatch.GetElapsedTime(first.Value.Created) >= Lifetime)
        {
            _weight -= first.Value.Weight;
            _entries.Remove(first.Value.Key);
            _order.RemoveFirst();
        }
    }

    private sealed record Item(string Key, ModelContextEntry Entry, int Weight, long Created);
}
