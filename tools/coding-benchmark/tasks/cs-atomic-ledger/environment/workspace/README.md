# Atomic ledger

Run the visible smoke checks with `dotnet run --project smoke/Smoke.csproj`.
The public API lives under `src/`. Batch order is observable: each transfer
uses the balances produced by earlier transfers in that same batch.
