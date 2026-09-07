# Token Usage Telemetry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture provider-reported input/output/cache token usage for each Ru model step and persist it without confusing unknown usage with zero.

**Architecture:** Add one provider-neutral immutable usage value in Harness.Core. Model adapters attach usage to the terminal model delta, the managed Worker carries it through the v2 done envelope, and the loopback runner stores it alongside the assistant step. Existing estimated input tokens and old persisted payloads remain compatible.

**Tech Stack:** C#/.NET 10, System.Text.Json, xUnit, existing Worker v2 WebSocket protocol.

---

### Task 1: Define usage contracts and persistence shape

**Files:**
- Create: `backend-dotnet/RuAgent.Harness.Core/ModelUsage.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core/AssistantStep.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core/AssistantStepJsonCodec.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core/Context/ModelContextSnapshot.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core/Persistence/HarnessPayloadIntent.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core/Persistence/HarnessEventIntent.cs`
- Test: `backend-dotnet/RuAgent.Harness.Core.Tests/AssistantStepJsonCodecTests.cs`

- [x] Write tests proving exact zero is preserved, unknown usage serializes as absent/null, and old payloads without usage still deserialize.
- [x] Run the focused tests and observe the expected failure because `ModelUsage` and `AssistantStep.Usage` do not exist.
- [x] Add nullable provider-neutral fields for input, output, total, cache-read, and cache-write tokens; add optional `Usage` to `AssistantStep`; serialize/deserialize it without changing existing estimated-token fields.
- [x] Run the focused tests and the persistence tests.

### Task 2: Capture usage from provider streaming responses

**Files:**
- Modify: `backend-dotnet/RuAgent.Runtime.Adapters/Models/ModelDelta.cs`
- Modify: `backend-dotnet/RuAgent.Runtime.Adapters/Models/OpenAiCompatibleModelProvider.cs`
- Modify: `backend-dotnet/RuAgent.Runtime.Adapters/Models/OpenAiResponsesModelProvider.cs`
- Modify: `backend-dotnet/RuAgent.Runtime.Adapters/Models/AnthropicMessagesModelProvider.cs`
- Tests: the matching provider test files under `backend-dotnet/RuAgent.Runtime.Adapters.Tests/`

- [x] Add failing provider tests for OpenAI Chat Completions `usage`, OpenAI Responses `response.completed.usage`, and Anthropic `message_delta.usage`.
- [x] Run those tests to verify they fail because terminal deltas currently discard usage.
- [x] Add optional `ModelUsage` to `ModelDelta` and parse only provider-reported integer fields; keep `null` when a stream omits usage.
- [x] Run all adapter tests and confirm exact zero remains distinct from missing fields.

### Task 3: Carry usage through Worker v2 and the harness

**Files:**
- Modify: `backend-dotnet/RuAgent.Runtime.Worker/WorkerV2StepService.cs`
- Modify: `backend-dotnet/RuAgent.Runtime.Worker/WorkerV2Server.cs`
- Modify: `backend-dotnet/RuBackend/Harness/Worker/LoopbackModelStepRunner.cs`
- Modify: `backend-dotnet/RuAgent.Protocol/Harness/WorkerV2Contracts.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Persistence/HarnessSqliteMapper.cs`
- Tests: `backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerV2StepServiceTests.cs`, `backend-dotnet/RuBackend.Tests/Worker/WorkerV2ClientTests.cs`

- [x] Add failing tests asserting usage is included only in the terminal done envelope and becomes `ModelStepResult.Usage`.
- [x] Extend `WorkerV2StepResult`, the done envelope, and loopback decoding with nullable usage; do not synthesize usage when absent.
- [x] Run the Worker and loopback focused tests.

### Task 4: Persist and expose per-turn usage

**Files:**
- Modify: `backend-dotnet/RuAgent.Harness.Core/Prompting/ModelStepResult.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core/Context/ModelContextSnapshot.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core.Tests/AgentRunCoordinatorTests.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Persistence.Tests/ModelContextSourceTests.cs`

- [x] Add a failing coordinator test proving provider usage is stored on the resulting assistant step.
- [x] Thread nullable usage from `ModelStepResult` into `AssistantStep`, preserving the existing estimated-input estimate.
- [x] Persist final-reply usage as a sidecar `ModelUsage` payload without exposing raw pre-policy assistant text.
- [x] Emit a durable `model.usage_recorded` event pointing at the sidecar so authenticated clients can fetch it by payload reference.
- [x] Run coordinator and persistence tests.

### Task 5: Document measurement semantics and verify the repository

**Files:**
- Modify: `docs/superpowers/plans/2026-09-07-token-usage-telemetry.md`
- Modify: the current runtime/verification documentation that describes token accounting, if present.

- [x] Document that `Usage = null` means the provider did not report usage, while numeric zero is an exact provider value; estimated input remains an estimate.
- [x] Run formatting/static checks and the relevant .NET suites.
- [x] Inspect current documentation for outdated token-accounting claims before completion.
- [ ] Commit the focused change and verify `main` is clean before reporting.
