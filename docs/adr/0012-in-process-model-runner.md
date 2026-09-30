---
status: accepted
date: 2026-09-30
---

# Run model generation in the Host process

The private worker seam no longer earned its cost. The Public Host was its only
consumer, the renderer of every field on its wire, and the owner of the
credential store it read from. We decided that one model step runs inside the
ASP.NET Core Public Host through the typed `WorkerV2StepService` behind the
existing `IModelStepRunner` seam. The typed worker WebSocket protocol v2 and the
`RuAgent.Runtime.Worker.exe` executable are retired; `RuAgent.Runtime.Worker`
remains as the class library that owns the step service and its bounds.

The runner keeps the behavior the loopback transport carried: conversation
build, bounded three-attempt retry with 2 s/5 s backoff and selection feedback,
reset-frame semantics, monotonic delta sequence, receipt validation, and the
stable failure codes. `AgentProviderSnapshotFactory` returns
`ProviderBootstrapData` directly instead of writing a single-use DPAPI handoff
file; the Host assembles the typed `ModelGenerationRequest`, so the request
shape is validated as a type, not as parsed JSON.

## Trade-off accepted

The worker process was documented as isolation: it could crash without the
Host, and it was a separate address space from the credential store. Both
protections are given up. The Host already owned the credential store, the
settings transaction, and the provider catalog, and it rendered every step
result anyway, so moving generation in-process does not widen who can read a
key — the handoff protected credentials in transit between two processes, not
from the Host. A crash in the model step now fails the run in the Host process;
durable run state in SQLite, with the existing recovery scan on restart, is the
restart story.

## Consequences

- ADR-0002 (typed worker protocol v2) is superseded: no private
  model-generation wire contract remains.
- The "owns the private Runtime Worker" clause of ADR-0001 is amended: the
  managed Public Host owns the Broker child only.
- Broker isolation is untouched: ADR-0009's single execution authority remains
  the process-isolated `RuAgentBroker`, reached over its own authenticated
  loopback lease, and no tool execution moved into the Host.
- The deterministic scripted runner (`RU_SCRIPTED_MODEL`) is the only
  deterministic path; the `RU_HARNESS_WORKER_TOKEN` branch was removed with the
  worker.
