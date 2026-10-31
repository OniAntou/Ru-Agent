using System.Collections.Concurrent;
using System.Text.Json;
using AtomicLedger;

var groups = new List<GroupResult>
{
    Run("ordered_batch", OrderedBatch), Run("atomic_rejection", AtomicRejection),
    Run("idempotent_replay", IdempotentReplay), Run("version_conflicts", VersionConflicts),
    Run("overflow_and_funds", OverflowAndFunds), Run("snapshot_isolation", SnapshotIsolation),
    Run("concurrent_linearizability", ConcurrentLinearizability)
};
Console.WriteLine(JsonSerializer.Serialize(new { groups }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
return groups.All(group => group.Passed) ? 0 : 1;

static GroupResult Run(string name, Action<Checks> test)
{
    var checks = new Checks();
    try { test(checks); } catch (Exception error) { checks.Fail("uncaught " + error.GetType().Name); }
    return new GroupResult(name, checks.Failures.Count == 0, checks.Count, checks.Failures.Take(8).ToArray());
}
static void OrderedBatch(Checks c)
{
    var repeated = NewLedger(("A", 5), ("B", 0));
    var repeatedResult = repeated.Apply("repeat", 0, [new("A", "B", 1), new("A", "B", 1)]);
    c.That(repeatedResult.Code == "ok" && Balance(repeated.Snapshot(), "A") == 3
        && Balance(repeated.Snapshot(), "B") == 2, "repeated transfer pairs remain an ordered batch");
    var ledger = NewLedger(("A", 10), ("B", 0), ("C", 0));
    var result = ledger.Apply("order", 0, [new("A", "B", 7), new("B", "C", 5)]);
    c.That(result.Code == "ok" && result.Version == 1, "batch commits once");
    c.That(Balance(result, "A") == 3 && Balance(result, "B") == 2 && Balance(result, "C") == 5, "later transfer observes earlier credit");
    c.That(ledger.Apply("empty", 1, Array.Empty<Transfer>()).Code == "invalid", "empty batch rejected");
}
static void AtomicRejection(Checks c)
{
    var invalidOpeningBalanceRejected = false;
    try { _ = new AtomicLedger.AtomicLedger(new Dictionary<string, long> { ["negative"] = -1 }); }
    catch (ArgumentException) { invalidOpeningBalanceRejected = true; }
    c.That(invalidOpeningBalanceRejected, "negative opening balance is rejected");
    var ledger = NewLedger(("A", 8), ("B", 0), ("C", 0));
    var result = ledger.Apply("partial", 0, [new("A", "B", 3), new("A", "C", 9)]);
    c.That(result.Code == "insufficient_funds", "late shortage has named failure");
    c.That(ledger.Snapshot().Version == 0, "rejection does not advance version");
    c.That(Balance(ledger.Snapshot(), "A") == 8 && Balance(ledger.Snapshot(), "B") == 0, "rejection rolls back earlier transfer");
    c.That(ledger.Apply("partial", 0, [new("A", "B", 1)]).Code == "ok", "rejected ID is not reserved");
}
static void IdempotentReplay(Checks c)
{
    var ledger = NewLedger(("A", 9), ("B", 0));
    Transfer[] body = [new("A", "B", 4)];
    var first = ledger.Apply("same", 0, body);
    var replay = ledger.Apply("same", -100, body.ToArray());
    c.That(first.Code == "ok" && replay.Code == "ok", "same body replays despite stale expected version");
    c.That(replay.Version == first.Version && Balance(replay, "A") == 5 && ledger.Snapshot().Version == 1, "replay returns original commit");
    c.That(ledger.Apply("same", 1, [new("A", "B", 3)]).Code == "idempotency_conflict", "changed body conflicts");
    c.That(ledger.Apply("same", 1, [new("B", "A", 4)]).Code == "idempotency_conflict", "ordered body identity is retained");
}
static void VersionConflicts(Checks c)
{
    var ledger = NewLedger(("A", 10), ("B", 0));
    c.That(ledger.Apply("first", 0, [new("A", "B", 1)]).Code == "ok", "initial version accepted");
    var conflict = ledger.Apply("stale", 0, [new("A", "B", 2)]);
    c.That(conflict.Code == "version_conflict", "stale version rejected");
    c.That(conflict.Version == 1 && Balance(conflict, "A") == 9 && Balance(conflict, "B") == 1, "conflict returns current state");
}
static void OverflowAndFunds(Checks c)
{
    var overflow = NewLedger(("A", long.MaxValue), ("B", 1));
    var failed = overflow.Apply("overflow", 0, [new("B", "A", 1)]);
    c.That(failed.Code == "overflow", "credit overflow rejected");
    c.That(overflow.Snapshot().Version == 0 && Balance(overflow.Snapshot(), "A") == long.MaxValue, "overflow is atomic");
    var both = NewLedger(("A", long.MaxValue), ("B", 0));
    c.That(both.Apply("both", 0, [new("B", "A", 1)]).Code == "insufficient_funds", "debit funds take precedence over simultaneous credit overflow");
    var funds = NewLedger(("A", 0), ("B", 2));
    c.That(funds.Apply("funds", 0, [new("A", "B", 1)]).Code == "insufficient_funds", "debit shortage rejected");
    c.That(funds.Apply("negative", 0, [new("A", "B", -1)]).Code == "invalid", "non-positive transfer rejected");
    c.That(funds.Apply("self", 0, [new("A", "A", 1)]).Code == "invalid", "self transfer rejected");
    c.That(funds.Apply("unknown", 0, [new("A", "missing", 1)]).Code == "invalid", "unknown account rejected");
}
static void SnapshotIsolation(Checks c)
{
    var ledger = NewLedger(("A", 5), ("B", 0));
    var old = ledger.Snapshot();
    ledger.Apply("later", 0, [new("A", "B", 2)]);
    c.That(old.Version == 0 && Balance(old, "A") == 5 && Balance(old, "B") == 0, "old snapshot remains unchanged");
    var rejectedMutation = false;
    try { ((IDictionary<string, long>)old.Balances)["A"] = 99; }
    catch (Exception error) when (error is NotSupportedException or InvalidCastException) { rejectedMutation = true; }
    c.That(rejectedMutation && Balance(ledger.Snapshot(), "A") == 3, "snapshot cannot mutate ledger state");
}
static void ConcurrentLinearizability(Checks c)
{
    const int contenders = 32;
    var ledger = NewLedger(("A", 1000), ("B", 0));
    using var start = new ManualResetEventSlim(false);
    var results = new ConcurrentBag<BatchResult>();
    var tasks = Enumerable.Range(0, contenders).Select(i => Task.Run(() =>
    {
        start.Wait(); results.Add(ledger.Apply("race-" + i, 0, [new("A", "B", 1)]));
    })).ToArray();
    start.Set(); Task.WaitAll(tasks);
    c.That(results.Count(result => result.Code == "ok") == 1, "one same-version writer commits");
    c.That(results.Count(result => result.Code == "version_conflict") == contenders - 1, "remaining writers observe conflict");
    var final = ledger.Snapshot();
    c.That(final.Version == 1 && Balance(final, "A") == 999 && Balance(final, "B") == 1, "final state is one linearized batch");
}
static AtomicLedger.AtomicLedger NewLedger(params (string Name, long Balance)[] accounts) => new(accounts.ToDictionary(item => item.Name, item => item.Balance));
static long Balance(object value, string account) => value switch
{
    BatchResult result => result.Snapshot.Balances[account],
    LedgerSnapshot snapshot => snapshot.Balances[account],
    _ => throw new ArgumentException("unsupported balance view")
};
sealed class Checks
{
    public int Count { get; private set; }
    public List<string> Failures { get; } = [];
    public void That(bool condition, string message) { Count++; if (!condition) Failures.Add(message); }
    public void Fail(string message) { Count++; Failures.Add(message); }
}
sealed record GroupResult(string Name, bool Passed, int Checks, string[] Failures);
