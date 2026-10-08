namespace AtomicLedger;

public sealed class AtomicLedger
{
    private readonly Dictionary<string, long> _balances;
    private long _version;

    public AtomicLedger(IReadOnlyDictionary<string, long> openingBalances)
    {
        _balances = new Dictionary<string, long>(openingBalances);
    }

    public BatchResult Apply(string requestId, long expectedVersion, IReadOnlyList<Transfer> transfers)
    {
        foreach (var transfer in transfers)
        {
            if (transfer.Amount <= 0 || !_balances.ContainsKey(transfer.From) || !_balances.ContainsKey(transfer.To))
                return Result("invalid", Snapshot());
            _balances[transfer.From] -= transfer.Amount;
            _balances[transfer.To] += transfer.Amount;
        }
        _version++;
        return Result("ok", Snapshot());
    }

    public LedgerSnapshot Snapshot() => new(_version, new Dictionary<string, long>(_balances));

    private static BatchResult Result(string code, LedgerSnapshot snapshot) => new(code, snapshot.Version, snapshot);
}
