namespace AtomicLedger;

public sealed record Transfer(string From, string To, long Amount);
public sealed record LedgerSnapshot(long Version, IReadOnlyDictionary<string, long> Balances);
public sealed record BatchResult(string Code, long Version, LedgerSnapshot Snapshot)
{
    public bool Succeeded => Code == "ok";
}
