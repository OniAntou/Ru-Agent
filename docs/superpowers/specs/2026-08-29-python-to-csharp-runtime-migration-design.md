# Python-to-C# Runtime Migration Design

**Date:** 2026-08-29
**Status:** Historical design record. Deterministic Phases 1–2 and the managed
Phase 3 production cleanup shipped on `main` by 2026-08-30; native acceptance
remains a separate user-owned gate. See the [Phase 3 retirement plan](../plans/2026-08-30-python-runtime-phase-3-retirement.md).

## Goal

Move Ru's production runtime behavior from Python to C#/.NET without changing
the public ASP.NET Core contract, the Agent protocol, privacy rules, or the
observable behavior of the desktop application. C#/.NET becomes the product
control plane and the default runtime. A model or audio engine that has no
stable managed implementation may remain temporarily as an isolated provider
process; it must not remain a second business-logic runtime.

## Scope

The migration covers production code currently under `src/ru_runtime` that is
reachable from the managed runtime:

- Agent generation, prompt composition, context budgets, compaction, model
  tool selection, hosted-provider adapters, and deterministic intent parsing;
- memory, activity, diagnostics, credentials, and other persistence behavior
  not already owned by the C# Harness Persistence layer;
- the private Worker v2 Agent path;
- the private Worker v1 runtime/device path, including voice, STT, VTube
  Studio, observation, and their lifecycle/locking rules;
- managed startup, process ownership, cancellation, and safe error mapping.

The first implementation task must build an import/startup inventory so that
legacy Python modules are not ported merely because they still exist in the
tree. Existing C# persistence and protocol implementations are extended rather
than duplicated when they already own the same contract.

Benchmark programs, migration helpers, deterministic fixtures, and test-only
harness scripts remain Python unless they are invoked by the product build or
release path. Model weights and voice assets are data, not migration targets.

## Non-goals

- Rewriting the complete system in C or C++ in this project.
- Replacing an ML/audio engine solely to remove an interpreter when a typed
  provider boundary can preserve behavior safely.
- Changing public routes, SSE/WebSocket envelopes, Agent error codes, UI
  behavior, or user-facing privacy policy.
- Building the future OS-enforced filesystem/network sandbox. That remains a
  separate security project; path policy and request metadata are not a
  sandbox.
- Adding extension, plugin, MCP, subagent, or LSP functionality.

## Target architecture

The existing ASP.NET Core Public Host and `RuAgent.Broker` remain the public
and policy authorities. The new managed runtime is split into focused projects:

| Boundary | Responsibility | Allowed dependencies |
| --- | --- | --- |
| `RuAgent.Protocol` | Versioned JSON/WebSocket contracts, limits, identifiers, and stable errors | BCL and shared contract types only |
| `RuAgent.Runtime.Core` | Pure generation orchestration, prompt/context composition, compaction, deterministic parsers, and storage interfaces | `RuAgent.Protocol`; no device or process APIs |
| `RuAgent.Runtime.Worker` | Private authenticated Worker v2/v1 endpoints, request ownership, streaming, cancellation, and resource locks | `Runtime.Core`, `Protocol`, ASP.NET/WebSocket hosting |
| `RuAgent.Runtime.Adapters` | Cloud model clients, audio/STT, VTS, observation, and external-provider adapters | `Runtime.Core`, `Protocol`, bounded OS/network APIs |
| `RuBackend` | Public routes, sessions, history, approvals, SSE, and lifecycle composition | Worker/Broker clients and existing Harness services |

The managed host owns the Worker and Broker children in the existing Windows
kill-on-close Job Object. There is no public Python HTTP endpoint and no direct
Python import from C#. During migration, a Python provider can be a child of
the owned process tree, but its interface is a versioned, bounded protocol.

The steady-state flow is:

```text
RuApp / RuAgent.Cli
    -> ASP.NET Core Public Host (:8000)
    -> authenticated C# Runtime Worker (loopback)
    -> Runtime.Core
       -> IModelProvider
       -> tool/skill/web adapters
       -> storage interfaces
       -> audio/VTS/observation adapters
```

During Phase 1, the C# Worker v2 endpoint and the legacy Python Worker v1
endpoint may run on separate dynamic loopback ports. The internal launch plan
and dependency injection carry both URLs; the public runtime descriptor and
public route contract remain unchanged. After Phase 2, one C# Worker owns both
protocol surfaces; the current retirement diff removes the production Python
endpoint and leaves only test/provider seams that Phase 3 will classify or
delete.

## Module mapping

The following mapping is the migration boundary. Exact class names may evolve
inside the stated project, but the responsibility and dependency direction are
fixed.

| Current Python area | Managed destination | Phase | Migration rule |
| --- | --- | --- | --- |
| `generation/context.py`, `prompt_context.py`, `system_prompt_composer.py` | `Runtime.Core/Generation` and `Runtime.Core/Prompting` | 1 | Preserve seven-layer ordering, receipt digest, bounds, and redaction |
| `generation/turns.py`, `tool_calls.py`, `volume_intent.py`, `search.py` | `Runtime.Core/Generation` | 1 | Keep parser semantics and stable invalid-input errors |
| `generation/runner.py`, `openai.py`, `orchestration.py` | `Runtime.Adapters/Models` plus `Runtime.Core/Generation` | 1 | Keep provider-neutral request/result types; network implementation is C# |
| `worker/v2/protocol.py`, `model_step.py`, `handlers.py` | `Runtime.Worker/V2` | 1 | Use the existing `RuAgent.Protocol` v2 contract without a wire fork |
| `memory/*`, `persistence/*` | `Runtime.Core/Storage` or existing `RuAgent.Harness.Persistence` | 1 | One owner per database/outbox; no parallel stores or dual writes |
| `worker/v1/*` | `Runtime.Worker/V1` and `Runtime.Adapters` | 2 | Keep v1 compatibility until every device scenario has parity |
| `voice/*`, `speech/*` | `Runtime.Adapters/Audio` | 2 | Preserve pacing, cancellation, GPU ownership, and WAV bounds |
| `vts/*`, `observation/*` | `Runtime.Adapters/Devices` | 2 | Preserve private metadata filtering and reconnect behavior |
| `scripts/*`, `tests/*` | Python tooling unless product-coupled | 0 or later | Port only when build/release/runtime requires it |

## Interfaces and provider boundary

`Runtime.Core` exposes narrow interfaces so that deterministic behavior is
testable without a device or network:

```csharp
public interface IModelProvider
{
    IAsyncEnumerable<ModelDelta> GenerateAsync(
        ModelRequest request,
        CancellationToken cancellationToken);
}

public interface IVoiceProvider
{
    Task<VoiceResult> SynthesizeAsync(
        VoiceRequest request,
        CancellationToken cancellationToken);
}

public interface IRuntimeStore
{
    Task<T> ReadAsync<T>(string key, CancellationToken cancellationToken);
    Task WriteAsync<T>(string key, T value, CancellationToken cancellationToken);
}
```

`ModelRequest` contains the already-redacted prompt context, bounded turns,
capabilities, model settings, and cancellation/timeout policy. Providers return
bounded deltas or a typed result; raw provider exceptions never cross the
Worker boundary.

An engine that remains Python temporarily is wrapped by a provider adapter
using a versioned framed protocol over owned stdin/stdout (or an explicitly
configured loopback endpoint where the engine requires HTTP). The boundary
must define a hello/version handshake, maximum frame size, request ID,
sequence, cancellation, timeout, and stable error mapping. The provider cannot
read Ru's database, tokens, or private VTS/audio metadata except for the
explicit request fields.

## Protocol and lifecycle compatibility

- `RuAgent.Protocol` remains the single source of truth for all wire types and
  limits.
- Worker v2 continues to use `hello`, `agent.step.start`, bounded deltas,
  `agent.step.done`, `agent.step.cancel`, and `error` with the existing
  sequence and authentication rules.
- Worker v1 remains available during Phases 1 and 2 for device/runtime calls;
  it is never exposed publicly.
- Cancellation is cooperative end-to-end. The C# Worker must await provider
  ownership before completing cleanup; it must not detach a running provider
  task or leave a GPU/audio lock held.
- The existing process manager remains the owner of all children. Shutdown,
  readiness failure, and crash paths terminate the complete process tree.
- The migration selector is a typed internal configuration value with two
  temporary values (`PythonCompatibility` and `Dotnet`). It is not a user
  mode, is never accepted from an untrusted public request, and is removed
  after Phase 3.
- No automatic Python fallback is allowed. A failed C# provider returns a
  stable error unless an operator explicitly selected compatibility mode.

## Rollout phases

### Phase 0: baseline and inventory

1. Trace managed startup, imports, subprocesses, and route-to-worker calls.
2. Mark each Python module as active production, provider-only, test/tooling,
   or legacy.
3. Capture golden fixtures for prompt composition, context compaction, model
   selection, tool calls, memory/persistence, protocol errors, cancellation,
   and privacy filtering.
4. Record p50/p95 timings and peak memory for deterministic operations and
   provider calls separately.

This phase changes no runtime behavior and produces the reference artifacts
used by later parity tests.

### Phase 1: managed Core and Agent v2

1. Add `RuAgent.Runtime.Core`, `RuAgent.Runtime.Worker`,
   `RuAgent.Runtime.Adapters`, and their focused test projects.
2. Port deterministic generation, prompt/context, compaction, parser, and
   provider-request behavior first.
3. Implement the authenticated C# Worker v2 endpoint against the current
   `RuAgent.Protocol` contract.
4. Extend the managed launch plan to start C# v2 and Python v1 as separate
   private children during the compatibility window.
5. Route Agent model steps to C# v2 while voice, STT, VTS, and observation
   continue through Python v1.
6. Enable the C# v2 path by default only after all Phase 1 gates pass.

Phase 1 is complete when Agent text/coding/tool steps have golden parity,
bounded cancellation, stable errors, privacy parity, and no public contract
change. Python v2 is then no longer part of managed startup, although the
explicit compatibility selector remains temporarily available for rollback.

### Phase 2: managed device and voice adapters

1. Port Worker v1 request validation, runtime settings, lifecycle, and locking.
2. Port STT, voice chunking/pacing/streaming, VTube Studio actions, and
   observation adapters behind `Runtime.Adapters` interfaces.
3. Keep OmniVoice or another ML engine behind the typed provider boundary until
   the managed/native implementation passes audio quality and resource gates.
4. Move the default v1 endpoint to the C# Worker and run automated plus manual
   microphone, WAV, VTS, and observation acceptance.

Phase 2 is complete when every supported device scenario has contract and
manual parity, private metadata remains isolated, and the C# process owns the
same cancellation and Job Object semantics.

### Phase 3: Python retirement

1. Remove Python from the default `WorkerBrokerLaunchPlan` and managed build.
2. Delete only Python production modules proven unreachable by import/startup
   scans and the full compatibility suite.
3. Retain Python test/benchmark tools that are not product-coupled, with docs
   identifying them as tooling.
4. Remove the temporary compatibility selector and update architecture,
   operations, build, and verification docs.

Phase 3 is complete when a clean managed installation starts without Python,
all product routes and supported device scenarios pass, and no production
package imports `ru_runtime`.

## Error, privacy, and security rules

- Malformed or oversized input is rejected before provider invocation.
- Provider timeout, unavailable, protocol, cancellation, and generation
  failures map to bounded stable codes; raw exceptions, URLs with credentials,
  response bodies, and secrets do not cross the boundary.
- Logs and activity events contain safe summaries and bounded identifiers only.
  Raw prompts, code, paths, audio, transcripts, credentials, and VTS/voice
  metadata remain outside visible history and diagnostics.
- Existing policy, approval ordering, workspace trust, web SSRF protections,
  and Skills explicit-grant rules remain authoritative. Migration cannot use a
  language change to bypass them.
- One generation owns the existing GPU/audio locks for its full provider call;
  cancellation releases them deterministically.
- Child processes inherit only the current allowlisted environment and are
  terminated with the owning Job Object.

## Verification and acceptance gates

Every phase uses the same evidence classes:

1. **Unit:** pure C# behavior for parsers, prompt layers, compaction, bounds,
   storage serialization, and error mapping.
2. **Contract:** strict round-trip and malformed-payload tests against
   `RuAgent.Protocol` fixtures, including sequence, cancellation, and frame
   limits.
3. **Golden parity:** the same bounded inputs run through Python reference and
   C# implementation; text, tool calls, receipts, errors, and redaction must
   match the approved fixture fields.
4. **Integration:** Public Host, Worker, Broker, SSE, process ownership, and
   restart/rollback tests using dynamic loopback ports.
5. **Privacy:** diagnostic/activity/history scans prove that private metadata
   and credentials are absent from visible or durable outputs.
6. **Performance:** deterministic and provider p50/p95/peak-memory results are
   compared with Phase 0; a phase may not regress a measured path by more than
   10% without an explicit performance decision recorded in its plan.
7. **Native acceptance:** real microphone, audio playback, OmniVoice, VTS, and
   observation checks remain explicit Windows acceptance and are never inferred
   from deterministic tests.

The current public contract must remain `56 HTTP + 1 WebSocket` throughout the
migration. Heavy Python and .NET suites run sequentially to avoid resource
contention. A phase cannot become the default until all of its gates pass; a
failed gate leaves the previous implementation selected explicitly.

## Documentation and maintenance

Each implementation phase updates the current architecture, runtime behavior,
operations, build, and verification documents before its commit. Historical
plans remain historical and are not used as runtime contracts. New classes are
kept small and responsibility-focused; when a file begins to combine protocol,
provider, persistence, and device concerns, the implementation plan must split
it before adding more behavior.

The OS-enforced filesystem/network sandbox remains a separate project and is
not claimed as a result of this migration.
