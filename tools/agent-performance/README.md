# Agent loop performance probe

Windows-only, credential-free workload for Host continuation costs and request
prefix stability. Uses generated 2 KiB files, a synthetic prompt/model and an
in-process HTTP handler. SQLite, encrypted payloads, exact request admission,
native serialization and Broker read execution use production code. No user
settings, instructions, credentials or recorded conversations are loaded.

From the repository root:

```powershell
.\scripts\benchmark_agent_loop.ps1
.\scripts\benchmark_agent_loop.ps1 -Samples 3 -Steps 60 -CompareCache
```

The script builds this probe in Release and runs fresh processes sequentially:
three samples each at 16, 32 and 60 steps by default. Every step executes one
real read. Halfway through each sample the model runner and context source are
reconstructed from durable state; this is not an OS-process crash/restart test.
The synthetic provider always asks for another read, so the workload is bounded
by the fixture rather than a completed user task.

Each adjacent request must retain all preceding messages and the same tools/model
request envelope, including across reconstruction. All steps must remain successful/running and cached mode must
exercise the Host cache. First four steps and the reconstruction step are
excluded from warm percentiles. Every sample's warm step p95 must stay below
the explicit, deliberately generous 2,000 ms Host regression budget. Cold start
and reconstruction times are reported separately. The gate does not require a
cache speedup on every machine or compare .NET timings with DSH's Node timings.

`-CompareCache` runs an otherwise identical uncached variant and alternates mode
order between samples. Report per-sample warm step/context p50/p95, cold costs,
cache hits, OS/architecture/CPU count and prefix comparisons; retain raw samples.
The script shares the test-matrix mutex and exits 75 on contention. Do not overlap
with publishing, another benchmark or a running process/loopback matrix.

Logs and `report.json` go under `artifacts/agent-performance/<timestamp>` by
default (`-OutputDirectory` overrides it). CI preserves that directory even on
failure. To verify the gate itself, a separate `-Samples 1 -Steps 16
-MaxWarmStepP95Ms 1` run should fail on a normal machine; retain its report and
do not treat that intentional diagnostic failure as a passing workload.

Low-level invocation after build:

```powershell
dotnet tools/agent-performance/bin/Release/net10.0-windows/AgentPerformance.dll --steps=60 --restart-at=30
```

The low-level entry point does not acquire the matrix lock. Its generated
temporary root is checked before recursive cleanup. Source responsibilities:
`Program.cs` owns the fixture lifecycle; `ProbeSupport.cs` owns timing wrappers
and synthetic HTTP; `SyntheticPrompt.cs` owns deterministic context and the
bounded performance-meter subscription. Provider latency, KV-cache hit rate and
GUI rendering are outside this workload. See [performance diagnostics](../../docs/operations/agent-performance.md).
