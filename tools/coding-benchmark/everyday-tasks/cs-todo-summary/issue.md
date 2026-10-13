# Pending work summary
Implement the existing `TodoBoard.CountPendingByOwner()` placeholder in
`src/TodoBoard.cs`. Count only items with Done=false. Trim owner names; null,
empty or whitespace-only names belong to `(unassigned)`. Owner matching is
ordinal and case-sensitive. Enumerate returned keys in ordinal ascending order.
Return a fresh snapshot on each call so callers cannot affect later summaries.
Keep `Add` and `CountDone` behavior. Empty boards return an empty dictionary.
Run `dotnet run --project smoke/Smoke.csproj`. No external dependencies.
