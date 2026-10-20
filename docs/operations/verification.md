# Verification

Run from the repository root. Choose checks by impact under [CONTRIBUTING.md](../../CONTRIBUTING.md#testing), preserve the first failure, and investigate before rerunning. One process/loopback/build matrix may run at a time; keep the runner's machine-wide mutex and treat exit 75 as contention.

## Deterministic checks

```powershell
.\scripts\test_dotnet.ps1 -Configuration Debug
.venv\Scripts\python.exe -m pytest tests -q
.venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py
dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64 -p:Platform=x64 -p:SelfContained=true
```

Use `-ProjectFilter` for focused .NET checks during iteration. Cross-component durability, authority, protocol changes and releases require the full applicable gate. Python owns tooling/docs contracts; managed tests own production runtime behavior. GUI source guards cover explicit ownership only; presenter behavior tests do not prove native pixels or keyboard interaction.

## Exact-package acceptance

```powershell
.\build.bat
.\scripts\publish_smoke.ps1 -PublishedDirectory .\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish
```

Smoke the exact output being delivered, with isolated data/log roots and synthetic credentials. Status and graceful shutdown establish packaged Host lifecycle, not real-provider quality. Desktop/CLI installer validation belongs to [distribution and updates](distribution-and-updates.md).

## Evidence boundaries

Report commands, failures, skipped checks and artifact paths. Keep deterministic tests, native UI acceptance and real-provider task outcomes separate. Do not control the user's desktop without their request, read actual credentials for fixtures, retry a matrix until an unexplained failure disappears, or infer live outcomes from scripted providers. Secret-free diagnostics may record safe codes, counts and timings, never private payloads.

SQLite migration checks use the genuine released v18 DDL plus frozen development-source DDL; current databases must not be relabeled as old fixtures. Test upgrade/fresh equality, unsupported versions, rollback/retry, FK restoration, preserved encrypted rows and simultaneous initializer processes.

## Historical evidence

[Dated verification archive](../history/verification/README.md) preserves prior results and their original limitations. Historical counts and retired worker/media acceptance commands are not current gates. New changes should link scoped evidence rather than append an ever-growing chronology to this page.
