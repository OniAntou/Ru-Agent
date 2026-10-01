---
name: verify
description: Run the repository verification matrices one at a time
allowed-tools: bash
---

# Verify

Run these after every code change, one matrix at a time. The .NET suite takes
a machine-wide lock and exits 75 when another run holds it: that means wait
and retry, not a failure.

1. Fast compile check first:
   `dotnet build backend-dotnet/RuBackend/RuBackend.csproj -c Debug`
2. Full .NET matrix (12 projects, real loopback processes):
   `.\scripts\test_dotnet.ps1 -Configuration Debug`
   Targeted single project while iterating:
   `.\scripts\test_dotnet.ps1 -Configuration Debug -ProjectFilter "<substring>"`
   Skip the per-project rebuild when binaries are already built (e.g. by the IDE):
   add `-NoBuild`.
3. Documentation and route guards:
   `.venv\Scripts\python.exe -m pytest tests -q`

Never run two matrices in parallel. A failing project stops the run; fix it
before moving on. Never weaken a contract test to go green.
