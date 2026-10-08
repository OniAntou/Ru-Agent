using System.Collections.ObjectModel;

namespace AtomicLedger;

public sealed class AtomicLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _balances;
    private readonly Dictionary<string, Receipt> _receipts = new(StringComparer.Ordinal);
    private long _version;

    public AtomicLedger(IReadOnlyDictionary<string, long> openingBalances)
    {
        ArgumentNullException.ThrowIfNull(openingBalances);
        if (openingBalances.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0))
            throw new ArgumentException("Opening balances must have names and be non-negative.", nameof(openingBalances));
        _balances = new Dictionary<string, long>(openingBalances, StringComparer.Ordinal);
    }

    public BatchResult Apply(string requestId, long expectedVersion, IReadOnlyList<Transfer> transfers)
    {
        if (string.IsNullOrWhiteSpace(requestId) || transfers is null)
            return Result("invalid", Snapshot());
        var body = transfers.ToArray();
        lock (_gate)
        {
            if (_receipts.TryGetValue(requestId, out var previous))
                return previous.Matches(body) ? previous.Result : Result("idempotency_conflict", SnapshotUnsafe());
            var invalid = Validate(body);
            if (invalid is not null) return Result(invalid, SnapshotUnsafe());
            if (expectedVersion != _version) return Result("version_conflict", SnapshotUnsafe());
            var proposed = new Dictionary<string, long>(_balances, StringComparer.Ordinal);
            var failure = ApplyToCopy(proposed, body);
            if (failure is not null) return Result(failure, SnapshotUnsafe());
            _balances.Clear();
            foreach (var pair in proposed) _balances.Add(pair.Key, pair.Value);
            _version++;
            var committed = Result("ok", SnapshotUnsafe());
            _receipts.Add(requestId, new Receipt(body, committed));
            return committed;
        }
    }

    public LedgerSnapshot Snapshot()
    {
        lock (_gate) return SnapshotUnsafe();
    }

    private string? Validate(IReadOnlyList<Transfer> transfers)
    {
        if (transfers.Count == 0) return "invalid";
        foreach (var transfer in transfers)
            if (transfer is null || string.IsNullOrWhiteSpace(transfer.From) || string.IsNullOrWhiteSpace(transfer.To)
                || transfer.From == transfer.To || transfer.Amount <= 0
                || !_balances.ContainsKey(transfer.From) || !_balances.ContainsKey(transfer.To)) return "invalid";
        return null;
    }

    private static string? ApplyToCopy(Dictionary<string, long> balances, IReadOnlyList<Transfer> transfers)
    {
        try
        {
            foreach (var transfer in transfers)
            {
                if (balances[transfer.From] < transfer.Amount) return "insufficient_funds";
                balances[transfer.From] = checked(balances[transfer.From] - transfer.Amount);
                balances[transfer.To] = checked(balances[transfer.To] + transfer.Amount);
            }
            return null;
        }
        catch (OverflowException) { return "overflow"; }
    }

    private LedgerSnapshot SnapshotUnsafe() => new(_version,
        new ReadOnlyDictionary<string, long>(new Dictionary<string, long>(_balances, StringComparer.Ordinal)));

    private static BatchResult Result(string code, LedgerSnapshot snapshot) => new(code, snapshot.Version, snapshot);

    private sealed class Receipt(Transfer[] body, BatchResult result)
    {
        public BatchResult Result { get; } = result;
        public bool Matches(IReadOnlyList<Transfer> candidate) => body.SequenceEqual(candidate);
    }
}
