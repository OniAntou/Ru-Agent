using AtomicLedger;

var ledger = new AtomicLedger.AtomicLedger(new Dictionary<string, long> { ["A"] = 10, ["B"] = 0 });
var result = ledger.Apply("smoke", 0, [new Transfer("A", "B", 3)]);
if (!result.Succeeded || result.Snapshot.Balances["A"] != 7 || result.Snapshot.Balances["B"] != 3)
    throw new InvalidOperationException("basic transfer smoke check failed");
Console.WriteLine("visible smoke checks passed");
