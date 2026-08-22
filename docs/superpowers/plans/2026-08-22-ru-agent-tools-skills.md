# Ru Agent Tools and Skills Implementation Plan

Status: Historical roadmap; the prior broad usable-tool and skill claim is withdrawn. Current operating truth is [Agent tools and truthful execution](../../operations/agent-tools.md).

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Build a process-isolated RuAgentBroker that lets Ru use bounded Desktop/files, workspace Terminal, Web/research, and Personal workflow tools through declarative skills, session-scoped permissions, explicit approvals, and deterministic Harness coverage.

**Architecture:** Add a .NET 10 private Broker process and a shared typed protocol. The Public Host remains the public contract and session owner; the Runtime Worker emits structured tool requests; the Broker validates policy and runs isolated adapters. The work is split into independent vertical workstreams so each slice can be tested and committed without touching unrelated voice, VTS, memory, or current Harness changes.

**Tech Stack:** .NET 10 class libraries/console apps, ASP.NET Core, C# WebSocket envelopes, WinUI 3, Python 3.13 Worker Contract, System.Text.Json, Windows process/job controls, xUnit, pytest, and the existing deterministic backend/GUI Harness.

---

## Execution boundaries

> **Current boundary (2026-08-23):** Broker, process, policy, approval, and
> protocol scaffolding exists, but this roadmap's broad usable-tool/skill claim
> is withdrawn. Action kernel v1 exposes only `system.volume@1.0.0` to the
> model. Ten typed bindings are executable only inside the Broker;
> `app.open`, connectors, and the eight unbound roadmap tools are absent;
> skills are disabled. Every future vertical slice requires its own
> authoritative receipt, deterministic contract/adapter evidence, and explicit
> native or external acceptance before becoming model-enabled.

The approved design is:

D:\LLM\docs\superpowers\specs\2026-08-22-ru-agent-tools-skills-design.md

The implementation must preserve these existing boundaries:

- Public Host owns the authenticated public HTTP/WebSocket contract.
- Runtime Worker owns generation, Voice Engine integration, and existing VTS/voice runtime implementations.
- RuAgentBroker owns agent tools, skills, policy, approvals, adapter execution, and broker cleanup.
- Chat contains no tool arguments, credentials, shell output, VTS metadata, or connector payloads.
- No unrestricted shell, admin/elevation, unrestricted delete, or persistent global permission is added; raw `terminal.shell` always requires exact-request approval.
- Terminal is workspace-only, non-admin, non-interactive by default, and has bounded timeout/output/process cleanup.
- Heavy Python/.NET suites run sequentially on Windows to avoid pagefile/OOM failures.
- The existing dirty worktree is user-owned. At plan authoring time, do not stage, reset, edit, or delete these unrelated paths:
  docs/operations/logging.md
  docs/operations/runtime-behavior.md
  docs/superpowers/plans/2026-08-21-ru-handsfree-voice-conversation.md
  docs/superpowers/specs/2026-08-21-ru-handsfree-voice-conversation-design.md
  gui-winui3.Tests/GuiDiagnosticWriterTests.cs
  gui-winui3.Tests/MeteorShowerFieldContractTests.cs
  gui-winui3.Tests/VoiceTurnCaptureServiceTests.cs
  gui-winui3/App.xaml
  gui-winui3/Controls/MeteorShowerField.cs
  gui-winui3/MainWindow.xaml
  gui-winui3/MainWindow.xaml.cs
  gui-winui3/Services/GuiDiagnosticWriter.cs
  gui-winui3/Services/VoiceTurnCaptureService.cs
  gui-winui3/Views/ChatView.xaml
  tests/test_cleanup_policy.py
  tools/maintenance/cleanup_policy.py
  artifacts/

The workstreams below are intentionally independent. Execute them in order and
stop at each verification/commit checkpoint.

## Workstream map

| Workstream | Outcome | Depends on |
| --- | --- | --- |
| 1. Protocol and Broker foundation | Private Broker process with health, session descriptors, auth, cleanup, and safe artifacts | None |
| 2. Host/Worker agent loop | Structured tool requests/results, session grants, approvals, cancellation, and safe Chat events | 1 |
| 3. Policy and declarative skills | Manifest validation, registry, risk matrix, bounded Orchestrator, approval tokens | 1 and 2 |
| 4. Desktop and workspace Terminal | Files/app/system tools plus typed terminal execution and raw-shell approval | 1–3 |
| 5. Web and research | Search/fetch/extract/download/note tools with untrusted-content handling | 1–4 |
| 6. Personal workflow | Isolated calendar/tasks/mail connectors with one-time side-effect approval | 1–5 |
| 7. GUI/Harness and rollout docs | Agent mode UI, activity/approval/stop states, deterministic checks, CI/manual gates | 1–6 |

## Canonical contracts

The implementation must keep these names stable across C#, Python, GUI, and Harness code:

~~~text
AgentEnvelope
AgentMessageTypes
AgentSessionGrant
AgentApprovalRequest
AgentApprovalResponse
ToolManifest
ToolRequest
ToolResult
SkillManifest
SkillRun
AgentStatusEvent
~~~

Envelope identity is always protocol_version, message_type, request_id,
session_id, correlation_id, deadline_ms, and payload. A result status is one of
succeeded, failed, cancelled, denied, approval_required, or timed_out. No
implementation may invent a parallel marker protocol after the structured
request path is connected.

# Workstream 1: Protocol and Broker foundation

Files:

- Create backend-dotnet/RuAgent.Protocol/RuAgent.Protocol.csproj.
- Create backend-dotnet/RuAgent.Protocol/AgentEnvelope.cs.
- Create backend-dotnet/RuAgent.Protocol/AgentMessageTypes.cs.
- Create backend-dotnet/RuAgent.Protocol/AgentProtocolException.cs.
- Create backend-dotnet/RuAgent.Protocol/AgentEnvelopeCodec.cs.
- Create backend-dotnet/RuAgent.Protocol/AgentToolContracts.cs.
- Create backend-dotnet/RuAgent.Broker/RuAgent.Broker.csproj.
- Create backend-dotnet/RuAgent.Broker/Program.cs.
- Create backend-dotnet/RuAgent.Broker/Configuration/BrokerOptions.cs.
- Create backend-dotnet/RuAgent.Broker/Runtime/BrokerSession.cs.
- Create backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs.
- Create backend-dotnet/RuAgent.Broker/Runtime/BrokerCleanup.cs.
- Create backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs.
- Create backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj.
- Create backend-dotnet/RuAgent.Broker.Tests/AgentEnvelopeTests.cs.
- Create backend-dotnet/RuAgent.Broker.Tests/BrokerSessionTests.cs.
- Create backend-dotnet/RuAgent.Broker.Tests/BrokerCleanupTests.cs.
- Modify gui-winui3/Services/BackendLaunchPlan.cs.
- Modify gui-winui3/Services/BackendProcessManager.cs.
- Modify gui-winui3/Services/ManagedRuBackendSessionProvider.cs.
- Modify gui-winui3/Services/IRuBackendSessionProvider.cs.
- Modify gui-winui3.Tests/BackendLaunchPlanTests.cs.
- Modify gui-winui3.Tests/BackendProcessManagerTests.cs.
- Modify build.bat.

- [ ] Step 1: Add failing protocol and lifecycle tests

Write tests before implementation. The envelope tests must assert the exact wire
shape and rejection rules:

~~~csharp
[Fact]
public void Serialize_round_trips_identity_and_payload()
{
    var envelope = new AgentEnvelope(
        1,
        "tool-1",
        AgentMessageTypes.ToolRequest,
        "session-1",
        "corr-1",
        10_000,
        new JsonObject { ["tool_id"] = "files.list" });

    var parsed = AgentEnvelopeCodec.Parse(AgentEnvelopeCodec.Serialize(envelope));

    Assert.Equal(envelope.ProtocolVersion, parsed.ProtocolVersion);
    Assert.Equal(envelope.RequestId, parsed.RequestId);
    Assert.Equal(envelope.MessageType, parsed.MessageType);
    Assert.Equal(envelope.SessionId, parsed.SessionId);
    Assert.Equal(envelope.CorrelationId, parsed.CorrelationId);
    Assert.Equal(envelope.DeadlineMs, parsed.DeadlineMs);
    Assert.Equal(envelope.Payload.ToJsonString(), parsed.Payload.ToJsonString());
}

[Theory]
[InlineData("unsupported_version")]
[InlineData("invalid_id")]
[InlineData("unknown_type")]
[InlineData("invalid_payload")]
public void Parse_rejects_invalid_envelopes(string expectedCode)
{
    var json = Fixtures.InvalidEnvelopeFor(expectedCode);
    var error = Assert.Throws<AgentProtocolException>(() => AgentEnvelopeCodec.Parse(json));
    Assert.Equal(expectedCode, error.Code);
}
~~~

Lifecycle tests must prove that a session descriptor is restricted to its root,
expires, is consumed once, and cleanup is idempotent after startup failure,
normal close, cancellation, and child failure.

Run:

~~~powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release
~~~

Expected: the new test project fails to build because the protocol and Broker
types do not exist yet. Do not proceed if the failure is restore-related.

- [ ] Step 2: Create the shared protocol project and exact envelope types

Create a net10.0 class library with nullable reference types and no web-host
dependency. The public records must be equivalent to:

~~~csharp
public sealed record AgentEnvelope(
    int ProtocolVersion,
    string RequestId,
    string MessageType,
    string SessionId,
    string? CorrelationId,
    int DeadlineMs,
    JsonObject Payload);

public static class AgentMessageTypes
{
    public const string Hello = "hello";
    public const string HelloOk = "hello_ok";
    public const string SessionOpen = "session_open";
    public const string SessionClose = "session_close";
    public const string SkillStart = "skill_start";
    public const string SkillEvent = "skill_event";
    public const string SkillResult = "skill_result";
    public const string ToolRequest = "tool_request";
    public const string ApprovalRequired = "approval_required";
    public const string ApprovalResponse = "approval_response";
    public const string ToolResult = "tool_result";
    public const string Cancel = "cancel";
    public const string Health = "health";
    public const string HealthOk = "health_ok";
}
~~~

Put the typed `ToolRequest`, `ToolResult`, and `AgentStatusEvent` records in
`AgentToolContracts.cs`. Their JSON payloads must retain the same snake_case
wire names and bounded result statuses as the envelope; adapters must not
smuggle untyped dictionaries around the Broker boundary.

AgentEnvelopeCodec must validate protocol version 1, non-empty request IDs no
longer than 128 characters, non-empty session IDs, deadlines from 1 to 300000
milliseconds, known message types, and object payloads. It must deep-clone
parsed JSON so callers cannot mutate the codec parse tree. Serialize and parse
must use the spec's explicit snake_case wire names (`protocol_version`,
`message_type`, `request_id`, `session_id`, `correlation_id`, `deadline_ms`,
and `payload`) and omit a null `correlation_id`.

Run the focused Broker tests again. Expected: all envelope tests pass and
lifecycle tests still fail only on missing Broker runtime types.

- [ ] Step 3: Add Broker session descriptor and private WebSocket host

Implement BrokerOptions, BrokerSession, AgentConnection, and BrokerHost. The
descriptor must contain only base_url, auth_token, protocol_version,
session_root, and expires_at. BrokerSession.Load must reject a missing token,
a non-absolute session root, a root outside the resolved temp session, or an
expired descriptor.

BrokerHost must bind to a dynamic loopback port unless an explicit diagnostic
port is supplied, require the generated bearer on every connection, answer
hello and health, and reject tool/skill requests with broker_not_ready until
the session has passed readiness. Use bounded frame size and cancellation
tokens; do not add a public ASP.NET route.

The minimal connection loop is:

~~~csharp
await foreach (var envelope in connection.ReadAsync(cancellationToken))
{
    AgentEnvelope reply = envelope.MessageType switch
    {
        AgentMessageTypes.Hello => ProtocolReplies.HelloOk(envelope),
        AgentMessageTypes.Health => ProtocolReplies.HealthOk(envelope),
        AgentMessageTypes.SessionOpen => session.Open(envelope),
        AgentMessageTypes.SessionClose => session.Close(envelope),
        _ => ProtocolReplies.Error(envelope, "broker_not_ready"),
    };
    await connection.SendAsync(reply, cancellationToken);
}
~~~

Run:

~~~powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release
dotnet build D:\LLM\backend-dotnet\RuAgent.Broker\RuAgent.Broker.csproj -c Release --no-restore
~~~

Expected: protocol, session, and cleanup tests pass with zero warnings/errors.

- [ ] Step 4: Add Broker process ownership and safe result envelope

Extend the existing desktop composition root so the managed backend launch plan
starts `RuAgentBroker` as a third Job Object child, after the Runtime Worker
and before the Public Host. Generate a broker-only descriptor/token and a
session-root under the existing runtime data boundary; pass the descriptor
location to the Public Host through the owned process/session seam, not through
visible chat or a public route. Expose the broker descriptor to the Host-side
`AgentBrokerClient`, and make normal shutdown, startup failure, unexpected
child exit, and GUI disposal close the Broker before deleting only its owned
descriptor/root. Extend the existing launch-plan/manager tests to assert child
ordering, descriptor boundary, no token leakage into logs, Job Object cleanup,
and ordinary chat fallback when Broker startup fails. Update `build.bat` to
publish `RuAgent.Broker.exe` and fail before GUI publish if the Broker artifact
is missing. The deterministic backend Harness must use the same owned startup
and cleanup contract.

Implement BrokerCleanup so it stops the listener, cancels active requests,
terminates only Broker-owned adapter children, closes the port, deletes only
the generated session descriptor/root, and can run twice without throwing.

Add a SafeAgentArtifact writer that allows only component status, check ID,
tool ID, duration, exit status, and bounded error code. Reject keys containing
token, secret, prompt, response, audio, transcript, body, or credential,
matching the existing Harness event privacy pattern.

Add tests for duplicate cleanup, wrong bearer, wrong session, oversized output,
descriptor deletion, port closure, and secret-key rejection. Run the full
Broker test project and git diff --check.

- [ ] Step 5: Commit the foundation slice

Inspect only the new protocol/Broker and owned launcher paths, then commit:

~~~powershell
git add -- backend-dotnet/RuAgent.Protocol backend-dotnet/RuAgent.Broker backend-dotnet/RuAgent.Broker.Tests gui-winui3/Services/BackendLaunchPlan.cs gui-winui3/Services/BackendProcessManager.cs gui-winui3/Services/ManagedRuBackendSessionProvider.cs gui-winui3/Services/IRuBackendSessionProvider.cs gui-winui3.Tests/BackendLaunchPlanTests.cs gui-winui3.Tests/BackendProcessManagerTests.cs build.bat
git diff --cached --check
git commit -m "feat: add isolated Ru agent broker foundation"
~~~

# Workstream 2: Public Host/Worker agent loop

Files:

- Modify backend-dotnet/RuBackend/RuBackend.csproj to reference RuAgent.Protocol.
- Modify backend-dotnet/RuBackend/Worker/WorkerMessageTypes.cs.
- Modify backend-dotnet/RuBackend/Worker/WorkerModels.cs.
- Modify backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs.
- Create backend-dotnet/RuBackend/Agent/IAgentBrokerClient.cs.
- Create backend-dotnet/RuBackend/Agent/AgentBrokerClient.cs.
- Create backend-dotnet/RuBackend/Agent/AgentModels.cs.
- Create backend-dotnet/RuBackend/Agent/AgentSessionGrantStore.cs.
- Create backend-dotnet/RuBackend/Agent/AgentApprovalStore.cs.
- Modify backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs.
- Create backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs.
- Modify backend-dotnet/RuBackend/Conversation/ChatEvent.cs.
- Modify backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs.
- Modify src/ru_runtime/worker/v1/protocol.py.
- Modify src/ru_runtime/worker/v1/handlers.py.
- Modify src/ru_runtime/worker/v1/server.py.
- Modify src/ru_runtime/generation/orchestration.py.
- Create src/ru_runtime/generation/tool_calls.py.
- Test in backend-dotnet/RuBackend.Tests/Agent/ and
  tests/test_worker_protocol.py/tests/test_worker_server.py.

- [ ] Step 1: Specify the structured Worker messages and Host models

Add failing C# and Python contract tests for these exact message names:

~~~text
chat.tool.request
chat.tool.result
chat.tool.cancel
~~~

The C# records must be equivalent to:

~~~csharp
public sealed record WorkerToolRequest(
    string RequestId,
    string SessionId,
    string CorrelationId,
    string ToolId,
    string ToolVersion,
    JsonObject Arguments,
    int DeadlineMs);

public sealed record WorkerToolResult(
    string RequestId,
    string Status,
    JsonObject Output,
    string? ErrorCode,
    string SafeSummary);
~~~

The Host records must be equivalent to:

~~~csharp
public sealed record AgentSessionGrant(
    string SessionId,
    IReadOnlySet<string> Scopes,
    IReadOnlySet<string> SkillIds,
    DateTimeOffset ExpiresAt);

public sealed record AgentApprovalRequest(
    string RequestId,
    string SessionId,
    string ToolId,
    string ToolVersion,
    string ArgumentsHash,
    string SafeSummary,
    DateTimeOffset ExpiresAt);
~~~

Run the focused C# and Python contract tests. Expected: red failures for the
new message types and records, with all existing tests still runnable.

- [ ] Step 2: Add Worker request/result handling without marker execution

Extend WorkerMessageTypes, Python protocol.py, and handlers.py so a chat turn
can emit one validated chat.tool.request, suspend generation, accept a matching
chat.tool.result, and continue. Reject mismatched request/session/correlation
IDs, expired deadlines, duplicate results, and result payloads over the
configured limit. Cancellation must emit chat.tool.cancel and complete the
same cancellation path as existing chat cancellation.

Add tool_calls.py with one pure parser that accepts only a structured JSON tool
call object and returns ToolCall(tool_id, tool_version, arguments). It must not
parse or execute APP_OPEN, VOLUME, or SEARCH markers in the new path. Keep
marker parsing behind a temporary compatibility branch until Workstream 4
migrates the Desktop and Web adapters.

Run sequentially:

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_worker_protocol.py tests/test_worker_server.py
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~Agent
~~~

- [ ] Step 3: Add the Host-to-Broker client and session grant validation

Implement IAgentBrokerClient with OpenSessionAsync, RunSkillAsync,
ExecuteToolAsync, RespondToApprovalAsync, CancelAsync, and CloseSessionAsync.
Each method must carry the Host session ID, correlation ID, deadline, and
cancellation token. AgentBrokerClient must prove the Broker token and protocol
version during connection, reject a response for a different session/request,
and map Broker statuses to the canonical result statuses.

AgentSessionGrantStore must keep grants in memory only, expire them on read,
and expose TryGet(sessionId, requiredScope) without returning the token.
AgentApprovalStore must compute a SHA-256 hash over the canonical JSON tool
ID/version/arguments and accept a response only when request ID, session ID,
hash, and expiry match.

Add fake Broker tests for correct grant, missing scope, expired grant, approval
replay, changed arguments, timeout, cancellation, and stale result suppression.

- [ ] Step 4: Extract a bounded AgentTurnCoordinator from ConversationCoordinator

Do not grow ConversationCoordinator.cs with tool loops. Add
AgentTurnCoordinator with this interface:

~~~csharp
public interface IAgentTurnCoordinator
{
    IAsyncEnumerable<ChatEvent> HandleToolTurnAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken);
}
~~~

It must translate Worker tool requests into Broker calls, publish only safe
agent.activity and agent.approval_required events, wait for the GUI approval
response through the existing authenticated /chat connection, and resume the
Worker with a typed result. Ordinary chat must bypass the Agent loop when Agent
mode is off. Existing visible text, session history, memory, voice, VTS, and
no-emoji behavior must remain unchanged.

Extend ChatEvent and ChatWebSocketEndpoint with bounded agent
control/event messages without adding a new public route. Unknown agent control
messages, approval responses for another session, and oversized safe summaries
must be rejected without affecting an ordinary chat turn.

Add endpoint/coordinator tests for Agent mode off, activity order,
approval-required order, approve/reject, stop, cancel, stale response, and
ordinary-chat regression.

- [ ] Step 5: Commit the Host/Worker loop

Run the full backend test project and the focused Python Worker tests
sequentially. Then commit only the claimed protocol, Host Agent, Worker, and
conversation paths:

~~~powershell
git add -- backend-dotnet/RuAgent.Protocol backend-dotnet/RuBackend/RuBackend.csproj backend-dotnet/RuBackend/Worker/WorkerMessageTypes.cs backend-dotnet/RuBackend/Worker/WorkerModels.cs backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs backend-dotnet/RuBackend/Agent backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs backend-dotnet/RuBackend/Conversation/ChatEvent.cs backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs src/ru_runtime/worker/v1/protocol.py src/ru_runtime/worker/v1/handlers.py src/ru_runtime/worker/v1/server.py src/ru_runtime/generation/orchestration.py src/ru_runtime/generation/tool_calls.py
git diff --cached --check
git commit -m "feat: add structured Ru agent tool loop"
~~~

# Workstream 3: Policy and declarative skills

Files:

- Create backend-dotnet/RuAgent.Broker/Policy/RiskClass.cs.
- Create backend-dotnet/RuAgent.Broker/Policy/ToolScope.cs.
- Create backend-dotnet/RuAgent.Broker/Policy/ToolPolicyEngine.cs.
- Create backend-dotnet/RuAgent.Broker/Policy/ApprovalTokenService.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/ToolManifest.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/ToolRegistry.cs.
- Create backend-dotnet/RuAgent.Broker/Skills/SkillManifest.cs.
- Create backend-dotnet/RuAgent.Broker/Skills/SkillRun.cs.
- Create backend-dotnet/RuAgent.Broker/Skills/SkillRegistry.cs.
- Create backend-dotnet/RuAgent.Broker/Skills/SkillOrchestrator.cs.
- Create backend-dotnet/RuAgent.Broker/Skills/SkillPackageLoader.cs.
- Create backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs.
- Create backend-dotnet/RuAgent.Broker.Tests/ManifestTests.cs.
- Create backend-dotnet/RuAgent.Broker.Tests/SkillOrchestratorTests.cs.

- [ ] Step 1: Add manifest and policy red tests

Test rejection of duplicate IDs, unsupported schema versions, missing risk,
unknown scopes, untrusted adapter names, undeclared skill tools, missing
limits, unbounded step counts, and executable skill package content. Test the
policy matrix:

~~~csharp
[Theory]
[InlineData(RiskClass.Read, false)]
[InlineData(RiskClass.Reversible, false)]
[InlineData(RiskClass.External, true)]
[InlineData(RiskClass.Destructive, true)]
public void Policy_matches_approval_requirement(RiskClass risk, bool approval)
{
    var decision = new ToolPolicyEngine().Evaluate(
        Fixtures.Grant(scopes: ["workspace.write"]),
        Fixtures.Manifest(risk, scopes: ["workspace.write"]));

    Assert.Equal(approval, decision.RequiresApproval);
}
~~~

- [ ] Step 2: Implement registry and session policy

Implement ToolManifest, ToolRegistry, RiskClass, ToolScope, and
ToolPolicyEngine. Registry validation must enforce id, exact semantic version,
adapter, risk, scopes, input/output schema, timeout, output limit, trust class,
and workspace/network declarations. Policy must distinguish read, reversible,
external, and destructive, and must never downgrade a manifest's declared
risk.

Implement ApprovalTokenService so its token binds request ID, session ID,
tool ID/version, canonical argument hash, and an expiry no longer than 60
seconds. Store only a token hash and consume it atomically.

- [ ] Step 3: Implement declarative Skill Registry

Load skill.json plus instructions.md from built-in or signed/allowlisted
directories. SkillManifest must contain ID/version, input/output schemas,
required tool IDs/scopes, ordered steps, approval policy, max steps, timeout,
and trust class. A step may be call_tool, validate_result, branch, or
named_transform; there is no executable code field.

SkillPackageLoader must reject path traversal, duplicate versions, unsigned
executable files, undeclared tools/scopes, dynamic tool IDs, and max steps over
32. SkillRegistry must return immutable manifests and never expose package paths
to the model.

- [ ] Step 4: Implement bounded SkillOrchestrator

Implement SkillOrchestrator.RunAsync with an explicit state machine:

~~~text
SkillRunState.Selected
 -> InputValidated
 -> StepReady
 -> ToolRequested
 -> ApprovalRequired | ToolRunning
 -> ResultSanitized
 -> NextStep | Completed | Failed | Cancelled
~~~

Use a default limit of 8 steps and 120 seconds, with hard ceilings of 32 steps
and 5 minutes. Every tool call must pass ToolPolicyEngine and every result must
pass the safe result builder before the next step. A cancellation or stale
session stops the orchestrator and all active adapter calls.

Add deterministic fake-tool tests for successful workflow, approval pause,
rejection, step limit, timeout, tool failure, branch validation, cancellation,
and stale-result suppression.

- [ ] Step 5: Commit policy and skills

Run the Broker tests, backend Agent tests, and git diff --check, then commit:

~~~powershell
git add -- backend-dotnet/RuAgent.Broker backend-dotnet/RuAgent.Broker.Tests backend-dotnet/RuAgent.Protocol
git diff --cached --check
git commit -m "feat: add Ru agent policy and declarative skills"
~~~

# Workstream 4: Desktop and workspace Terminal

Files:

- Create backend-dotnet/RuAgent.Broker/Tools/WorkspacePathGuard.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/ProcessTreeRunner.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/Terminal/TerminalManifest.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/Terminal/TerminalAdapter.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/Terminal/ShellRuntimeResolver.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/Desktop/FileTools.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/Desktop/AppTools.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/Desktop/SystemTools.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs.
- Create backend-dotnet/RuAgent.Broker.Tests/WorkspacePathGuardTests.cs.
- Create backend-dotnet/RuAgent.Broker.Tests/ProcessTreeRunnerTests.cs.
- Create backend-dotnet/RuAgent.Broker.Tests/TerminalAdapterTests.cs.
- Create backend-dotnet/RuAgent.Broker.Tests/DesktopToolTests.cs.
- Modify src/ru_runtime/system/pc_tools.py only when the structured adapter path is ready.
- Modify src/ru_runtime/generation/orchestration.py only to remove the compatibility marker after structured Desktop/Web tools are green.

- [ ] Step 1: Add red tests for workspace and process safety

Test WorkspacePathGuard with a root file, nested file, parent traversal,
absolute path, alternate separator, symlink escape, missing path, and a path
outside the selected root. Test ProcessTreeRunner with a fake executable,
non-zero exit, timeout, cancellation, output limit, and child-process cleanup.

Expected safe result fields are status, exit_code, duration_ms, stdout, stderr,
output_truncated, and relative working_directory. Never include the raw
environment.

- [ ] Step 2: Implement typed terminal.run and terminal.shell

terminal.run accepts exactly shell, relative working_directory, executable,
arguments, and timeout_ms. Resolve the runtime explicitly:

~~~text
powershell -> powershell.exe -NoProfile -NonInteractive -Command ...
git-bash   -> bash.exe --noprofile --norc -lc ...
wsl        -> wsl.exe --cd <workspace> -- ...
~~~

Use a sanitized environment containing only required non-secret variables. Do
not pass RU_*, credential-store values, access tokens, or full user profile
variables. Set the process working directory only after canonical path guard
success. Put the process in an owned Windows Job Object or equivalent process
tree tracker so timeout/cancellation cannot leave detached children.

terminal.shell accepts a raw script only when the exact request has a valid
one-time approval token. It uses the same workspace root, runtime resolver,
timeout, output, network, and cleanup limits as terminal.run.

- [ ] Step 3: Implement Desktop adapters and named operations

Register these manifests with explicit scopes and limits:

~~~text
system.time          system.read       read
system.status        system.read       read
app.list             system.read       read
app.open             app.open           reversible
files.list           workspace.read    read
files.read           workspace.read    read
files.write          workspace.write   reversible
files.move           workspace.write   reversible
workspace.search     workspace.read    read
clipboard.read       clipboard.read    read
clipboard.write      clipboard.write   reversible
system.volume.set    system.write      reversible
terminal.run         terminal.execute  reversible
terminal.shell       terminal.execute  destructive
~~~

The initial executable allowlist includes only bounded diagnostic/build/test
subcommands for git, rg, dotnet, pytest, and the selected shell runtime.
Repository mutation (`git add`, `git commit`, `git reset`, `git checkout`, and
`git push`), package restore/clone/fetch/upload, arbitrary delete, registry
changes, admin/elevation, and process detachment are not registered in v1.
Network-enabled terminal commands require `terminal.network` and one-time
approval.

- [ ] Step 4: Migrate current marker actions without changing visible chat

Add adapter-backed structured calls for the existing open-app, volume, and
search behavior. Keep the old marker branch only for clients that have not yet
negotiated protocol v1. Once the C# and Python Worker contract tests prove all
structured calls, remove APP_OPEN, VOLUME, and SEARCH marker execution from
src/ru_runtime/generation/orchestration.py.

Add regression tests proving marker strings cannot leak into visible chat,
history, TTS, memory, or tool arguments. Existing pc_tools.py unit tests must
remain green; the new adapters may call its narrowly scoped implementation only
through typed wrappers.

- [ ] Step 5: Commit Desktop/Terminal slice

Run sequentially:

~~~powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_worker_protocol.py tests/test_worker_server.py
git diff --check
~~~

Commit only the new Broker tool paths and explicitly claimed Worker migration:

~~~powershell
git add -- backend-dotnet/RuAgent.Broker backend-dotnet/RuAgent.Broker.Tests src/ru_runtime/system/pc_tools.py src/ru_runtime/generation/orchestration.py
git diff --cached --check
git commit -m "feat: add workspace desktop and terminal tools"
~~~

# Workstream 5: Web and research

Files:

- Create backend-dotnet/RuAgent.Broker/Tools/Web/WebSearchAdapter.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/Web/WebFetchAdapter.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/Web/WebContentSanitizer.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/Web/SourceCitation.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/Web/WebPolicy.cs.
- Create backend-dotnet/RuAgent.Broker/Tools/Notes/NotesAdapter.cs.
- Create backend-dotnet/RuAgent.Broker.Tests/WebAdapterTests.cs.
- Create backend-dotnet/RuAgent.Broker.Tests/WebPolicyTests.cs.
- Create skills/research.and_note/skill.json.
- Create skills/research.and_note/instructions.md.
- Modify src/ru_runtime/generation/orchestration.py to route structured
  web.search calls after the Desktop migration tests pass.

- [ ] Step 1: Add red tests for web safety and citations

Use a fake HttpMessageHandler; never call a live web service in unit tests. Test
bounded response size, timeout, non-HTML content, redirect policy,
localhost/private-IP rejection, unsupported scheme, script removal, prompt
injection treated as data, source title/URL extraction, and raw-body exclusion
from results.

- [ ] Step 2: Implement web adapters and untrusted-content boundary

web.search returns bounded source summaries. web.fetch returns sanitized text
plus source metadata. web.extract accepts only a previously fetched result ID.
web.download requires a workspace path and approval before writing. The
adapters must reject file:, loopback, link-local, private-network, and
unbounded redirect targets unless an explicit diagnostic allowlist is active.

Wrap all fetched text as:

~~~text
[untrusted_web_reference]
source_title: ...
source_url: ...
content: ...
[/untrusted_web_reference]
~~~

The Generation Kernel receives this as data and must not treat instructions
inside it as system or tool instructions.

- [ ] Step 3: Add the research skill and note approval

research.and_note must call web.search, web.fetch, and a named summary
transform. It must emit source citations and request approval before
notes.create/files.write. It may not silently save raw pages or unbounded
search results.

- [ ] Step 4: Commit the Web slice

Run Broker tests, the research skill fixture tests, Python Worker tests, and
the existing no-emoji/metadata/privacy tests sequentially. Commit:

~~~powershell
git add -- backend-dotnet/RuAgent.Broker backend-dotnet/RuAgent.Broker.Tests skills/research.and_note src/ru_runtime/generation/orchestration.py
git diff --cached --check
git commit -m "feat: add Ru web research tools"
~~~

# Workstream 6: Personal workflow connectors

Files:

- Create backend-dotnet/RuAgent.Connectors/RuAgent.Connectors.csproj.
- Create backend-dotnet/RuAgent.Connectors/Program.cs.
- Create backend-dotnet/RuAgent.Connectors/ConnectorSession.cs.
- Create backend-dotnet/RuAgent.Connectors/Credentials/ConnectorCredentialStore.cs.
- Create backend-dotnet/RuAgent.Connectors/Calendar/CalendarAdapter.cs.
- Create backend-dotnet/RuAgent.Connectors/Tasks/TasksAdapter.cs.
- Create backend-dotnet/RuAgent.Connectors/Mail/MailAdapter.cs.
- Create backend-dotnet/RuAgent.Connectors.Tests/ConnectorPolicyTests.cs.
- Create backend-dotnet/RuAgent.Connectors.Tests/CalendarAdapterTests.cs.
- Create backend-dotnet/RuAgent.Connectors.Tests/MailAdapterTests.cs.
- Create skills/personal.create-calendar-event/skill.json.
- Create skills/personal.create-calendar-event/instructions.md.
- Modify backend-dotnet/RuAgent.Broker only through the typed connector adapter seam; do not copy OAuth or credential code into the Broker.

- [ ] Step 1: Add fake connector and credential-boundary tests

Test that connector tokens never enter model payloads, Broker artifacts, logs,
Harness events, or exceptions. Test expired session, wrong connector scope,
preview/create separation, one-time approval, cancellation, and connector
process cleanup. Use fake OAuth responses and a fake service client.

- [ ] Step 2: Implement connector process and credential store

The connector process receives only a short-lived connector session descriptor,
not the full Agent session or model prompt. It loads credentials through the
existing Windows credential boundary, keeps them in memory, and returns only
typed metadata/result summaries. It must close the connector session and erase
temporary files on shutdown.

- [ ] Step 3: Implement preview-first calendar/tasks/mail adapters

Expose:

~~~text
calendar.list             calendar.read
calendar.preview_create  calendar.write, no side effect
calendar.create           calendar.write, one-time approval
tasks.list                tasks.read
tasks.create              tasks.write, one-time approval
tasks.complete            tasks.write, one-time approval
mail.search               mail.read
mail.draft                mail.draft, local/external draft only
mail.send                 mail.send, one-time approval
~~~

No adapter may send, create, complete, or delete an external record before the
Host has approved the exact arguments hash.

- [ ] Step 4: Commit the Personal workflow slice

Run connector tests, Broker policy tests, and the deterministic Harness with
all live side effects replaced by fakes. Commit only connector/skill paths:

~~~powershell
git add -- backend-dotnet/RuAgent.Connectors backend-dotnet/RuAgent.Connectors.Tests backend-dotnet/RuAgent.Broker skills/personal.create-calendar-event
git diff --cached --check
git commit -m "feat: add approved Ru personal workflow connectors"
~~~

# Workstream 7: GUI, Harness, and documentation integration

Files:

- Create gui-winui3/Services/AgentSessionService.cs.
- Create gui-winui3/Services/AgentActivityItem.cs.
- Create gui-winui3/Services/AgentApprovalModel.cs.
- Modify gui-winui3/Views/ChatView.xaml.
- Modify gui-winui3/Views/ChatView.xaml.cs.
- Modify gui-winui3/MainWindow.xaml only for the approved Agent mode shell
  control; preserve unrelated dirty UI work.
- Modify gui-winui3.Harness/Program.cs.
- Modify gui-winui3.Harness/UiAutomationScenario.cs.
- Modify gui-winui3.Harness/HarnessGuiResult.cs.
- Modify gui-winui3/Services/HarnessEventSink.cs with allowlisted agent event
  codes only.
- Modify scripts/harness/session.py to launch the fake-adapter Broker with the
  same descriptor, port, readiness, and cleanup contract as production.
- Modify scripts/harness/processes.py only if the Broker needs a distinct
  bounded child classification; preserve owned-tree termination semantics.
- Modify scripts/aspnetcore_compatibility_harness.py to expose the agent
  deterministic mode and safe artifact checks.
- Modify tests/test_harness_session.py and tests/test_harness_processes.py.
- Create tests/test_agent_harness.py for grant, approval, cancellation, stale
  result, cleanup, and privacy scenarios.
- Create gui-winui3.Tests/AgentSessionContractTests.cs.
- Create gui-winui3.Tests/AgentApprovalTests.cs.
- Create gui-winui3.Tests/AgentPrivacyContractTests.cs.
- Modify docs/operations/verification.md.
- Modify docs/operations/runtime-behavior.md.
- Modify README.md.
- Modify CONTEXT.md.

- [ ] Step 1: Add failing GUI contract tests

Assert stable automation IDs and safe state transitions:

~~~text
AgentModeButton
AgentScopePanel
AgentActivity
AgentApprovalCard
AgentApproveButton
AgentRejectButton
AgentStopButton
~~~

Tests must prove Agent mode is session-scoped, approval text excludes raw
credentials/arguments with secrets, stop cancels the active request, and an
agent error does not change ordinary Chat history. Do not modify current voice
automation IDs or the dirty voice/meteor files in this slice.

- [ ] Step 2: Implement session/activity/approval UI

Add a compact Agent mode control to Chat. Scope selection starts a session
grant for Desktop/Terminal, Web, or Personal. Activity rows show only safe
status, selected shell, relative cwd, tool ID, duration, and bounded output.
Approval shows the exact safe action, scope, target, data leaving the device,
undo availability, and expiry. It offers one-time approve, reject, details,
and a stop action.

The GUI must not persist the grant globally, display raw tool output in the
conversation transcript, or launch a Broker directly. Public Host events drive
the UI.

- [ ] Step 3: Extend deterministic GUI Harness scenarios

Add scenarios for:

~~~text
agent-read-only
agent-terminal-approval
agent-terminal-reject
agent-cancel
agent-stale-result
agent-private-metadata
~~~

Use fake Broker/adapters and the existing session descriptor/event path. Verify
approval, stop, cancellation, no late update, no credential leakage, and
cleanup. Do not launch real PowerShell, Git Bash, WSL, web accounts, mail,
calendar, microphone, speaker, or VTS in deterministic mode.

- [ ] Step 4: Update operational docs and domain vocabulary

Add the approved terms Agent Session, RuAgentBroker, Tool Manifest, Skill
Manifest, Session Grant, Approval Token, Workspace Terminal, and Connector
Adapter to CONTEXT.md. Update runtime behavior and verification docs with
exact commands, artifact interpretation, terminal limits, and the separate
live external-account/hardware gate. Update README entry points.

- [ ] Step 5: Commit GUI/Harness/docs integration

Run the focused GUI tests, backend tests, Broker tests, Python tests, route
matrix, and UI detector sequentially. Inspect the feature-only diff and stage
only claimed files. Commit:

~~~powershell
git add -- gui-winui3/Services/AgentSessionService.cs gui-winui3/Services/AgentActivityItem.cs gui-winui3/Services/AgentApprovalModel.cs gui-winui3/Views/ChatView.xaml gui-winui3/Views/ChatView.xaml.cs gui-winui3.Harness/Program.cs gui-winui3.Harness/UiAutomationScenario.cs gui-winui3.Harness/HarnessGuiResult.cs gui-winui3/Services/HarnessEventSink.cs gui-winui3.Tests/AgentSessionContractTests.cs gui-winui3.Tests/AgentApprovalTests.cs gui-winui3.Tests/AgentPrivacyContractTests.cs docs/operations/verification.md docs/operations/runtime-behavior.md README.md CONTEXT.md
git diff --cached --check
git commit -m "feat: add Ru agent session and approval UI"
~~~

# Final verification and release gate

- [ ] Step 1: Run all heavyweight checks sequentially

Use the repository's Windows runtime and run one command at a time:

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --no-restore --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output D:\LLM\artifacts\aspnetcore\ru-harness-agent.json
dotnet list D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj package --vulnerable --include-transitive
cmd /c D:\LLM\build.bat
~~~

Expected: existing route contract remains 41 HTTP routes and 2 WebSocket
routes; all tests pass; deterministic artifact has no secrets; self-contained
backend, Broker, and GUI outputs exist; no live account, microphone, speaker,
OmniVoice, or VTS claim is made.

- [ ] Step 2: Run static privacy and stale-reference scans

~~~powershell
rg -n -i "APP_OPEN|VOLUME|SEARCH:|token|secret|credential|raw command|request body|transcript" backend-dotnet/RuAgent.Broker backend-dotnet/RuBackend gui-winui3 src/ru_runtime --glob '!**/bin/**' --glob '!**/obj/**'
git diff --check
~~~

Review every match. Marker names are allowed only in migration tests/docs;
credentials and raw command/body references must not be emitted into visible
chat, history, memory, logs, or artifacts.

- [ ] Step 3: Perform user-owned manual acceptance

Do not launch or control the app automatically. The user manually verifies:

1. Agent mode starts and ends with the Chat session.
2. Ru can inspect and modify a selected workspace only after the appropriate
   grant/approval.
3. Terminal runs the selected PowerShell/Git Bash/WSL runtime in the workspace,
   while admin/system paths and credential environment remain inaccessible.
4. A timeout or Stop cancels child processes and produces no late update.
5. Web research cites sources and does not follow instructions embedded in a
   page.
6. Calendar/mail/task actions preview accurately and require one-time approval.
7. Live external-account, microphone, speaker, OmniVoice, and VTS behavior is
   tested separately from deterministic Harness success.

- [ ] Step 4: Verify branch scope before any push

~~~powershell
git status --short --branch
git diff --stat
git rev-list --left-right --count main...origin/main
~~~

Only feature/docs paths explicitly claimed by the current slice may be staged.
Unrelated dirty files remain untouched. Push to main only after the user
approves the final diff and all automated gates pass.

## Plan self-review checklist

- Spec coverage: process isolation is Workstream 1; Host/Worker loop is 2;
  policy and skills are 3; Desktop/Terminal is 4; Web is 5; Personal workflow
  is 6; GUI/Harness/docs are 7; final acceptance is explicit.
- Type consistency: AgentEnvelope, AgentSessionGrant, ToolManifest,
  ToolRequest, ToolResult, SkillManifest, and SkillRun are the canonical names;
  Worker messages are chat.tool.request, chat.tool.result, and chat.tool.cancel;
  Broker messages use the `tool_request`, `skill_*`, and `approval_*` names
  defined in Workstream 1, while Worker messages retain their existing dotted
  names.
- Safety coverage: workspace guard, no-admin process launch, sanitized env,
  approval hash, timeout, cancellation, output limits, child cleanup, and safe
  artifact redaction each have a named test step.
- Dirty-worktree safety: every commit command scopes paths and explicitly
  excludes the known unrelated files.
- Heavy verification: Python, backend, Broker, GUI, route, Harness, build, and
  vulnerability checks are sequential.
