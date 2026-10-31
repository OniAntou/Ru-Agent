namespace RepositoryLedger;

public sealed class AtomicLedger
{
    private readonly Dictionary<string, decimal> _balances;
    private readonly HashSet<string> _appliedBatches = [];

    public AtomicLedger(IEnumerable<KeyValuePair<string, decimal>> balances) =>
        _balances = balances.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public decimal Balance(string account) => _balances.TryGetValue(account, out var value) ? value : 0m;

    public bool ApplyBatch(string batchId, IReadOnlyList<Transfer> transfers) => false;
}
