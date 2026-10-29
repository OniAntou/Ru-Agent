namespace RepositoryLedger;

public sealed class AtomicLedger
{
    private readonly Dictionary<string, decimal> _balances;
    private readonly HashSet<string> _appliedBatches = [];

    public AtomicLedger(IEnumerable<KeyValuePair<string, decimal>> balances) =>
        _balances = balances.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public decimal Balance(string account) => _balances.TryGetValue(account, out var value) ? value : 0m;

    public bool ApplyBatch(string batchId, IReadOnlyList<Transfer> transfers)
    {
        if (string.IsNullOrWhiteSpace(batchId) || transfers is null || transfers.Count == 0) return false;
        if (_appliedBatches.Contains(batchId)) return true;
        var transferIds = new HashSet<string>(StringComparer.Ordinal);
        var next = new Dictionary<string, decimal>(_balances, StringComparer.Ordinal);
        foreach (var transfer in transfers)
        {
            if (transfer is null || string.IsNullOrWhiteSpace(transfer.Id) || !transferIds.Add(transfer.Id) ||
                string.IsNullOrWhiteSpace(transfer.From) || string.IsNullOrWhiteSpace(transfer.To) ||
                transfer.From == transfer.To || transfer.Amount <= 0) return false;
            var from = next.GetValueOrDefault(transfer.From);
            if (from < transfer.Amount) return false;
            next[transfer.From] = from - transfer.Amount;
            next[transfer.To] = next.GetValueOrDefault(transfer.To) + transfer.Amount;
        }
        _balances.Clear();
        foreach (var pair in next) _balances.Add(pair.Key, pair.Value);
        _appliedBatches.Add(batchId);
        return true;
    }
}
