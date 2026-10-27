---
name: verify
description: Select verification by change impact and run repository checks serially
allowed-tools: run_process, powershell
---

# Verify

Read CONTRIBUTING.md and choose verification by the affected behavior. Use
focused builds/tests for local code and relevant guards for documentation.
Shared protocol, durability, authority and release changes need the full
applicable matrix. Run one matrix at a time. The .NET suite takes
a machine-wide lock and exits 75 when another run holds it: that means wait
and retry, not a failure.

1. Fast compile check first:
   `dotnet build backend-dotnet/RuBackend/RuBackend.csproj -c Debug`
2. Full .NET matrix when required (13 projects, real loopback processes):
   `.\scripts\test_dotnet.ps1 -Configuration Debug`
   Targeted single project while iterating:
   `.\scripts\test_dotnet.ps1 -Configuration Debug -ProjectFilter "<substring>"`
   Skip the per-project rebuild only when the exact changed binaries are built:
   add `-NoBuild`.
3. Documentation and route guards:
   `.venv\Scripts\python.exe -m pytest tests -q`

Never run two matrices in parallel. A failing project stops the run; fix it
before moving on. Never weaken a contract test to go green.
