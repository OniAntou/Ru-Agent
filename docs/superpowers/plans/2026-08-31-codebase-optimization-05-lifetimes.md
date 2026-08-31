# Broker Authority and Worker Lifetime Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce responsibility drift in Broker execution and Worker cleanup without weakening policy or changing protocol semantics.

**Architecture:** Broker transport delegates internal execution orchestration without moving authority to another process. Worker v1/v2 keep separate adapters and share only equivalent request ownership/serialized-send mechanics.

**Tech Stack:** C#/.NET 10, ASP.NET TestServer, xUnit, existing private WebSockets.

## Global Constraints

Inherit [master constraints](2026-08-31-codebase-optimization.md). Rollout phase 3. These are architecture candidates, not proven speedups; retain a documented no-change outcome if extraction adds complexity without a useful seam.

**Resume status:** The prior implementation inventory rejected both B and W.
That outcome is preserved below. The detailed steps in this file are candidate
designs, not instructions to reopen rejected work automatically. Revisit only
with new evidence and user approval; otherwise retain the rejection and verify
that security/protocol contracts remain intact.

---

## B1 — Broker execution ownership

**Files:**

- Modify: `backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs`.
- Modify only justified orchestration: `backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs`.
- Modify composition if needed: `backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs`.
- Create: `backend-dotnet/RuAgent.Broker/Runtime/BrokerExecutionCoordinator.cs`.
- Extend: `backend-dotnet/RuAgent.Broker.Tests/ToolExecutionServiceTests.cs`, `SkillConnectionTests.cs`, `ApprovalTokenServiceTests.cs`.
- Add: `backend-dotnet/RuAgent.Broker.Tests/BrokerExecutionCoordinatorTests.cs`.
- Read all current Broker run/lease/policy tests discovered in this checkout.

**New interface:** Move cohesive handlers into these Broker-internal entry points, using existing `RuAgent.Protocol.AgentEnvelope`:

```csharp
Task<AgentEnvelope> EvaluateAsync(AgentEnvelope request, CancellationToken cancellationToken);
Task<AgentEnvelope> PrepareAsync(AgentEnvelope request, CancellationToken cancellationToken);
Task<AgentEnvelope> RespondToApprovalAsync(AgentEnvelope request, CancellationToken cancellationToken);
Task<AgentEnvelope> ExecuteAsync(AgentEnvelope request, CancellationToken cancellationToken);
```

Reuse current session/lease/token/catalog dependencies and the handler callback. No second session store, approval ledger, permission cache, or unconditional approved flag. ToolExecutionService retains adapter-specific validation/normalization until a justified extraction moves it intact.

- [ ] **Step 1 — Characterize authority before moving code.**

Record an invariant map for evaluate/prepare/respond/execute: session owner, lease, capability, canonical intent/hash/snapshot binding, token store/consumption, scope/schema, deadline/cancellation source, adapter, receipt/privacy.

Distinguish authorization-token and preparation-token paths from legacy tool-request/approval flow. Similar checks may enforce different contracts.

Run the full Broker baseline. Add missing behavior cases, not source snapshots:

| Mutation to catch | Expected observable behavior |
| --- | --- |
| Wrong session/run reuses valid token | Rejected before adapter invocation. |
| Intent/path/arguments/snapshot changes after prepare | Re-resolution rejects stale authorization. |
| Token reused | Stable stale result; no second invocation. |
| Approval response alone | Authorizes only, never executes. |
| Capability absent/workspace trust changed | Denied before execution. |
| Scope/schema invalid despite preparation | Existing denial preserved. |
| Session replacement close fails | No replacement execution or extra grant. |
| Adapter errors/cancels/deadline expires | Same bounded classification and receipt fields. |
| Skill invokes a tool | Same policy/approval, no bypass. |

Use real policy/token/session modules; replace only the external adapter callback with an invocation/order recorder. No fake coordinator authorizing everything. A pure extraction's characterization suite should pass before editing; missing lifecycle coverage must first expose any actual bug it targets.

- [ ] **Step 2 — Extract, preserving order.**

Move existing handler bodies and cohesive dependencies to BrokerExecutionCoordinator. Transport retains envelope validation, dispatch and wire/session lifecycle. Keep consume-before-adapter, re-resolution and exact errors. Do not delete validation for performance.

Maintain one-way composition; no cycles with ToolExecutionService. Keep state with its owner, not dozens of forwarding interfaces or giant partial files.

Integration pattern inside the existing tool-execute branch:

```csharp
return await _execution.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
```

This delegate is not authorization: the real moved implementation must remain behind it.

- [ ] **Step 3 — Verify and document.**

```powershell
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --nologo
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~AgentApproval|FullyQualifiedName~AgentPolicy"
```

Check every exit code and nonzero discovered test count. Compare every removed check against the invariant map: checks may move, not disappear. Record locality/ownership gains, not unmeasured throughput. Update architecture/Agent tool docs only where actual ownership changed.

## W1 — Shared connection ownership, distinct Worker semantics

**Files:**

- Modify: `backend-dotnet/RuAgent.Runtime.Worker/WorkerV1Server.cs`, `WorkerV2Server.cs`.
- Create: `backend-dotnet/RuAgent.Runtime.Worker/WorkerConnectionLifetime.cs`.
- Extend: `backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerV1ServerContractTests.cs`, `WorkerV2ServerContractTests.cs`.
- Add: `backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerConnectionLifetimeTests.cs`.

**New interface:** Narrow ownership surface; codecs/frame validation remain in each adapter.

```csharp
public WorkerConnectionLifetime(CancellationToken connectionCancellation, int? maxConcurrentRequests);
public bool TryStart(string requestId, Func<CancellationToken, Task> operation);
public bool TryCancel(string requestId);
public Task SendGroupAsync(Func<CancellationToken, Task> sendFrames, CancellationToken cancellationToken);
public Task StopAsync(CancellationToken cancellationToken);
```

v2 passes 1; v1 preserves existing concurrency, with null meaning no newly introduced cap. A v1 control+binary pair is one serialized send group. Duplicate-ID, sequence and error mapping remain adapter-owned; do not flatten all TryStart failures into the same error.

- [ ] **Step 1 — Characterization and lifecycle regression.**

Use the existing TestServer/fake provider helpers, never live cloud. Cover synchronous completion before registration returns, delayed completion, throwing operations, cancel during send, disconnect with an active request, stop racing new admission, duplicate IDs and next valid v2 start after completion.

Existing test `Server_rejects_wrong_token_without_exposing_secret` already protects authentication; reuse it, do not duplicate. Preserve `Server_authenticates_hello_and_emits_monotonic_done_sequence` expectations: hello sequence 0, first delta 1, completion 2 in its fixture.

A new pure ownership regression using the interface above:

```csharp
[Fact]
public async Task Completed_request_does_not_block_the_next_request()
{
    var lifetime = new WorkerConnectionLifetime(CancellationToken.None, 1);
    Assert.True(lifetime.TryStart("r1", _ => Task.CompletedTask));
    Assert.True(lifetime.TryStart("r2", _ => Task.CompletedTask));
    await lifetime.StopAsync(CancellationToken.None);
    Assert.False(lifetime.TryStart("r3", _ => Task.CompletedTask));
}
```

If completion bookkeeping is intentionally asynchronous, use an explicit completion signal owned by the operation/test instead of sleeps, and define the acceptance point precisely. Interface/compiler failure is not proof of the original runtime race.

- [ ] **Step 2 — Extract equivalent lifetime mechanics.**

Register ownership before invoking a possibly synchronous operation; remove once in finally. Serialize registry access; cancel/join stable snapshots instead of enumerating dictionaries while completions mutate them. Observe every task exception.

One owner closes admission, cancels requests, joins tasks, then disposes CTS/send gates. Cancelling a caller's StopAsync wait must not prematurely dispose shared state; keep underlying cleanup observed.

Preserve v1 binary pairing/concurrency, v2 one in-flight step, exact sequences, duplicate tracking, fragmented/oversized/invalid UTF-8 behavior and fixed-time authentication. Share buffer mechanics only if framing contracts are genuinely equivalent.

- [ ] **Step 3 — GREEN and scope gate.**

```powershell
dotnet test backend-dotnet/RuAgent.Runtime.Worker.Tests/RuAgent.Runtime.Worker.Tests.csproj -c Release --nologo
dotnet test backend-dotnet/RuAgent.Protocol.Tests/RuAgent.Protocol.Tests.csproj -c Release --nologo
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~WorkerV2Client|FullyQualifiedName~WorkerV2Contract"
```

Broker has a separate sequential receive/cancel path. Do not silently change it in a Worker refactor; reproduce independently and obtain agreement on changed semantics if needed.

**Exit:** Explicit lifetime/task/send ownership, no leaked/unobserved tasks in controlled fixtures, distinct protocols preserved. No automatic device/process control or commit/push.

## Preserved outcome from the 2026-08-31 inventory

The following is the preceding agent's recorded outcome, retained as history;
its test counts and conclusions were not revalidated by this planning task.

- **B (Broker):** Inspected `BrokerSession.cs`, `BrokerRunLease.cs`, `PolicyContext.cs`, `PendingApprovalStore.cs`, `ApprovalTokenService.cs`, `Tools/Files/*`, `Tools/Terminal/*`. Lease/token/scope checks are distinct per adapter (file vs terminal vs approval) and share only `CancellationTokenSource` dispose pattern. No equivalent `TryValidateLease` helper would reduce duplication without merging distinct `PolicyRule`/`PolicyMatcher` evaluation. **Rejected** as not justified; defense-in-depth retained. Tests: `RuAgent.Broker.Tests` 104 file/terminal/policy tests still pass.
- **W (Worker):** Inspected `RuAgent.Runtime.Worker` (v1 device + v2 agent) and `RuAgent.Protocol` framing. `WebSocket` close is already via `WebSocket.CloseAsync` with `CancellationToken`, `CTS` dispose via `using`, process-tree cleanup via `JobObject` owned by Public Host. v1/v2 framing (`agent.step.*` vs `runtime.*`) and concurrency (single vs multiple) are distinct; sharing `CloseGracefullyAsync` would hide `v1`'s `runtime.response` vs `v2`'s `agent.step.delta` differences. **Rejected** as not equivalent.

Historical checklist (reported by that inventory, not fresh verification):

- [x] B helper rejected with measured reason (no equivalent).
- [x] W helper rejected with measured reason (distinct framing).
- [x] All affected tests pass (Broker 104, Protocol/Worker 252+), no new package, no sandbox claim.
