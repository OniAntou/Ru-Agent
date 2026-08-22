# Ru Approval UI and One-Time Action Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Connect the existing Agent approval-required status to a real, safe,
one-time approval card in WinUI and the private Broker without exposing raw
arguments or enabling unrestricted shell execution.

**Architecture:** The Broker owns the authoritative in-memory pending approval
record containing the original typed request and canonical arguments hash. The
Public Host stores only bounded safe metadata, bridges an authenticated chat
control message to the Broker's existing approval_response protocol, and emits
safe result events. WinUI owns only presentation state and sends an opaque
approval ID plus the user's decision.

**Tech Stack:** .NET 10, ASP.NET Core WebSockets, System.Text.Json.Nodes,
WinUI 3/XAML, xUnit, the existing Python deterministic Harness, and the
repository's sequential Windows verification commands.

## Global Constraints

- Approval is explicit, one-time, session-bound, and short-lived; policy is re-evaluated at approval time.
- Raw arguments, scripts, credentials, connector bodies, bearer values, and raw tool output never enter visible chat, history, memory, logs, or Harness artifacts.
- The Broker keeps original arguments in memory only; the UI receives only approval_id, tool identity, safe action, scope, and expiry.
- terminal.run remains structured, workspace-only, allowlisted, bounded, non-admin, and approval-free; raw terminal.shell, web side effects, notes, and connector writes remain gated.
- No public HTTP route or direct Broker launch is added; all control traffic uses the authenticated Public Host WebSocket and private Broker WebSocket.
- Preserve unrelated dirty voice, meteor, cleanup, runtime-documentation, and artifact paths; stage only files claimed by the current task.
- Run heavyweight Python/.NET checks sequentially on Windows.
- Use .venv\Scripts\python.exe -m pytest, never bare pytest.

---

## Task 1: Broker pending approval and private protocol

**Files:**

- Create: backend-dotnet/RuAgent.Broker/Policy/PendingApprovalStore.cs
- Modify: backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs
- Modify: backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs
- Modify: backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs
- Create: backend-dotnet/RuAgent.Broker.Tests/PendingApprovalStoreTests.cs
- Modify: backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs
- Modify: backend-dotnet/RuAgent.Broker.Tests/TerminalAdapterTests.cs

**Interfaces:**

- PendingApprovalStore.Issue(AgentEnvelope request, ToolManifest manifest, JsonObject arguments, string safeAction, DateTimeOffset expiresAt, string? terminalApprovalToken) returns an opaque approval_id and stores cloned arguments plus any private terminal approval token in memory.
- PendingApprovalStore.TryConsume(string approvalId, string sessionId, bool approved, DateTimeOffset now, out PendingApproval? pending) atomically consumes a matching, unexpired record. It returns false for replay, wrong session, expiry, or unknown ID.
- ToolExecutionService.RespondToApprovalAsync(AgentEnvelope request, CancellationToken cancellationToken) consumes the pending record and either returns denied or dispatches the original typed request once with a private approval bypass.
- AgentConnection accepts an optional approval handler and routes AgentMessageTypes.ApprovalResponse only for an owned session.

- [ ] **Step 1: Write the failing store and protocol tests**

Add tests that create a real PendingApprovalStore and assert that an issued
record returns only bounded metadata, that approval consumes it once, and that
reject/expiry/session mismatch/replay never returns the original request. Add a
Broker WebSocket test that sends approval_response with only:

~~~json
{"approval_id":"approval-1","approved":true}
~~~

and currently fails because the Broker does not route that message.

- [ ] **Step 2: Run the focused tests and verify the expected RED failure**

Run:

~~~powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~PendingApproval|FullyQualifiedName~BrokerHost"
~~~

Expected: compilation or assertion failures specifically for the missing
pending store/approval handler, not restore or unrelated worktree failures.

- [ ] **Step 3: Implement the bounded in-memory pending store**

Use a ConcurrentDictionary<string, PendingApproval> and an atomic
TryRemove(KeyValuePair<...>). Clone the request arguments on issue and on
consume. Compute the canonical arguments hash inside the Broker from sorted
object keys and ordered arrays; do not accept a hash supplied by the GUI.
Expire records after 60 seconds and expose RemoveSession for Broker cleanup.

- [ ] **Step 4: Add approval-required metadata and one-time dispatch**

In ToolExecutionService, when policy returns RequiresApproval, issue a pending
record and return a result whose output is exactly the safe shape:

~~~csharp
new JsonObject
{
    ["approval_id"] = approvalId,
    ["tool_id"] = manifest.Id,
    ["tool_version"] = manifest.Version,
    ["scope"] = manifest.Category,
    ["safe_action"] = SafeAction(manifest),
    ["expires_at"] = expiresAt.ToString("O"),
}
~~~

Implement RespondToApprovalAsync so rejection consumes and returns denied;
approval consumes, rechecks the session/policy, and calls the normal dispatcher
with skipApproval: true. The dispatcher must receive an explicit approved flag:
notes.create passes approved: true only on this path, and terminal.shell builds
a TerminalShellRequest from the private pending arguments/token and calls
RunApprovedShellAsync. Construct ApprovalTokenService in this service and pass
it to TerminalAdapter; issue its token only for a terminal.shell pending record.
Other registered external/connector tools without an adapter remain safely
tool_not_implemented after approval and perform no side effect. Never put
original arguments or the private terminal token in the response envelope.

- [ ] **Step 5: Route the private approval_response message**

Extend AgentConnection and BrokerHost with an approval handler. Validate that
the session owns the request before calling it, return ToolResult with
approval_stale on mismatch/replay, and preserve request/session/correlation
identity in the response envelope.

- [ ] **Step 6: Run the focused tests and commit the Broker slice**

Run the focused command again and then:

~~~powershell
git diff --check -- backend-dotnet/RuAgent.Broker backend-dotnet/RuAgent.Broker.Tests
git add -- backend-dotnet/RuAgent.Broker/Policy/PendingApprovalStore.cs backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs backend-dotnet/RuAgent.Broker.Tests/PendingApprovalStoreTests.cs backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs backend-dotnet/RuAgent.Broker.Tests/TerminalAdapterTests.cs
git commit -m "feat: add one-time Broker approvals"
~~~

## Task 2: Public Host approval bridge

**Files:**

- Create: backend-dotnet/RuBackend/Agent/AgentApprovalCoordinator.cs
- Modify: backend-dotnet/RuBackend/Agent/AgentModels.cs
- Modify: backend-dotnet/RuBackend/Agent/IAgentBrokerClient.cs
- Modify: backend-dotnet/RuBackend/Agent/AgentBrokerClient.cs
- Modify: backend-dotnet/RuBackend/Agent/AgentApprovalStore.cs
- Modify: backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs
- Modify: backend-dotnet/RuBackend/Conversation/ChatEvent.cs
- Modify: backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs
- Modify: backend-dotnet/RuBackend/Program.cs
- Modify: backend-dotnet/RuBackend.Tests/Agent/AgentStoreTests.cs
- Modify: backend-dotnet/RuBackend.Tests/Agent/AgentBrokerClientTests.cs
- Modify: backend-dotnet/RuBackend.Tests/Conversation/ChatWebSocketEndpointTests.cs
- Modify: backend-dotnet/RuBackend.Tests/Conversation/ConversationCoordinatorTests.cs

**Interfaces:**

- AgentApprovalRequest contains only ApprovalId, SessionId, ToolId, ToolVersion, SafeAction, Scope, and ExpiresAt.
- AgentApprovalResponse contains only ApprovalId, SessionId, and Approved.
- IAgentBrokerClient.RespondToApprovalAsync(AgentApprovalResponse response, string? correlationId, int deadlineMs, CancellationToken cancellationToken) sends the private approval envelope without tool arguments.
- IAgentApprovalCoordinator.Register(WorkerToolRequest tool, AgentToolResult result) stores safe metadata only and returns a ChatEvent for the GUI.
- IAgentApprovalCoordinator.RespondAsync(AgentApprovalResponse response, CancellationToken cancellationToken) validates the Host-side session record and forwards the decision to the Broker.

- [ ] **Step 1: Write failing Host model and control-message tests**

Add tests for the safe approval model, one-time Host consumption, malformed
control messages, wrong session, oversized IDs, and the absence of arguments,
script, token, or credential fields in serialized ChatEvent output. Add a
WebSocket endpoint test that sends:

~~~json
{"type":"agent.approval.response","session_id":"session-1","approval_id":"approval-1","approved":false}
~~~

and expects a safe agent.approval_result event after the coordinator is wired.
Keep ordinary prompt parsing and history behavior unchanged.

- [ ] **Step 2: Run the Host tests and verify RED**

Run:

~~~powershell
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~Agent|FullyQualifiedName~ChatWebSocketEndpoint|FullyQualifiedName~ConversationCoordinator"
~~~

Expected: failures for the missing control-message branch and approval event
fields only.

- [ ] **Step 3: Refactor the Host approval models and client call**

Remove raw-argument approval inputs from IAgentBrokerClient. Keep the Host store
as bounded safe metadata for session validation; the Broker remains the
authority for the original arguments/hash. AgentBrokerClient sends:

~~~csharp
new JsonObject
{
    ["approval_id"] = response.ApprovalId,
    ["approved"] = response.Approved,
}
~~~

and accepts only a known ToolResultStatuses value plus a bounded summary.

- [ ] **Step 4: Register pending metadata from the agent tool turn**

When AgentTurnCoordinator receives approval_required, require the Broker output
to contain bounded approval_id, tool_id, tool_version, scope, safe_action, and
expires_at. Register those fields in AgentApprovalCoordinator and publish
agent.approval_required with the safe action as content and the remaining fields
as explicit JSON properties. If metadata is missing or oversized, return
approval_invalid and do not show a card.

- [ ] **Step 5: Add the authenticated chat control branch**

Extend ChatEvent with optional approval_id, tool_id, scope, and expires_at
properties. In ChatWebSocketEndpoint, parse control messages before prompt
messages, enforce the existing message-size bound, reject raw extra approval
fields, and call IAgentApprovalCoordinator. Send only agent.approval_result
with approval_id, status, and bounded safe summary. If a turn is active, return
busy without queuing the action.

- [ ] **Step 6: Run the Host tests and commit the bridge slice**

Run the focused Host command again, then:

~~~powershell
git diff --check -- backend-dotnet/RuBackend backend-dotnet/RuBackend.Tests
git add -- backend-dotnet/RuBackend/Agent/AgentApprovalCoordinator.cs backend-dotnet/RuBackend/Agent/AgentModels.cs backend-dotnet/RuBackend/Agent/IAgentBrokerClient.cs backend-dotnet/RuBackend/Agent/AgentBrokerClient.cs backend-dotnet/RuBackend/Agent/AgentApprovalStore.cs backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs backend-dotnet/RuBackend/Conversation/ChatEvent.cs backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs backend-dotnet/RuBackend/Program.cs backend-dotnet/RuBackend.Tests/Agent/AgentStoreTests.cs backend-dotnet/RuBackend.Tests/Agent/AgentBrokerClientTests.cs backend-dotnet/RuBackend.Tests/Conversation/ChatWebSocketEndpointTests.cs backend-dotnet/RuBackend.Tests/Conversation/ConversationCoordinatorTests.cs
git commit -m "feat: bridge Agent approvals through chat"
~~~

## Task 3: WinUI approval card and client state

**Files:**

- Modify: gui-winui3/Services/AgentApprovalModel.cs
- Modify: gui-winui3/Services/ChatWebSocketClient.cs
- Modify: gui-winui3/Views/ChatView.xaml
- Modify: gui-winui3/Views/ChatView.xaml.cs
- Modify: gui-winui3.Tests/AgentApprovalTests.cs
- Modify: gui-winui3.Tests/AgentSessionContractTests.cs
- Modify: gui-winui3.Tests/AgentPrivacyContractTests.cs
- Create: gui-winui3.Tests/ChatWebSocketAgentApprovalContractTests.cs

**Interfaces:**

- AgentApprovalModel exposes ApprovalId, SessionId, ToolId, SafeAction, Scope, ExpiresAt, and IsExpired(DateTimeOffset? now = null).
- ChatWebSocketClient.OnAgentApprovalRequired emits AgentApprovalModel; OnAgentApprovalResult emits (approvalId, status, safeSummary).
- ChatWebSocketClient.SendAgentApprovalAsync(string sessionId, string approvalId, bool approved, CancellationToken cancellationToken) sends only the control envelope.
- ChatView owns one nullable _pendingAgentApproval, three automation IDs, and no tool arguments.

- [ ] **Step 1: Write failing model/client/UI contract tests**

Extend the model test for empty/oversized IDs, expired approvals, and a safe
action containing Vietnamese text. Add source contract assertions for:

~~~text
AgentApprovalCard
AgentApproveButton
AgentRejectButton
agent.approval.response
~~~

Assert that the XAML does not bind an arguments, script, token, or credential
property and that the client serializer does not emit them.

- [ ] **Step 2: Run the GUI tests and verify RED**

Run:

~~~powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentApproval|FullyQualifiedName~ChatWebSocketAgentApproval"
~~~

Expected: failures for the missing approval card IDs/client method, not XAML
restore failures.

- [ ] **Step 3: Implement bounded WebSocket parsing and sending**

Parse agent.approval_required only when all safe fields are present and within
the existing 256-character UI bound. Ignore malformed frames without throwing.
Add SendAgentApprovalAsync with the exact payload:

~~~csharp
new
{
    type = "agent.approval.response",
    session_id = sessionId,
    approval_id = approvalId,
    approved,
}
~~~

Parse agent.approval_result and never pass its content into Messages.

- [ ] **Step 4: Implement the compact WinUI card**

Place a Border between AgentActivity and InputBorder, using the existing
surface/line/muted/ink resources, a subtle pending amber line, wrapping safe
action text, and two native Button controls. Add the three stable automation
IDs, accessible names, and a polite live region. Keep it collapsed until a
valid approval arrives; disable both buttons while sending.

- [ ] **Step 5: Wire state transitions and cleanup**

On a valid request, replace any previous pending record only if it belongs to
the current session. On approve/reject, set a short processing status, send
the control message, and clear the card on a terminal result. Clear it on
session reset, Agent toggle off, disconnect, expiry, and disposal. Do not add
an assistant/user chat message or Harness payload containing action arguments.
Keep existing voice/meteor controls untouched.

- [ ] **Step 6: Run GUI tests and commit the UI slice**

Run the focused GUI command again, then:

~~~powershell
git diff --check -- gui-winui3 gui-winui3.Tests
git add -- gui-winui3/Services/AgentApprovalModel.cs gui-winui3/Services/ChatWebSocketClient.cs gui-winui3/Views/ChatView.xaml gui-winui3/Views/ChatView.xaml.cs gui-winui3.Tests/AgentApprovalTests.cs gui-winui3.Tests/AgentSessionContractTests.cs gui-winui3.Tests/AgentPrivacyContractTests.cs gui-winui3.Tests/ChatWebSocketAgentApprovalContractTests.cs
git commit -m "feat: add Ru Agent approval card"
~~~

## Task 4: Deterministic Harness, operational docs, and regression coverage

**Files:**

- Modify: scripts/aspnetcore_compatibility_harness.py
- Modify: scripts/harness/session.py
- Modify: tests/test_agent_harness.py
- Modify: gui-winui3.Harness/UiAutomationScenario.cs
- Modify: gui-winui3.Harness/Program.cs
- Modify: gui-winui3.Harness/HarnessGuiResult.cs
- Modify: docs/operations/agent-tools.md
- Modify: docs/operations/verification.md
- Modify: README.md only if the approval control protocol changes the entry-point instructions

**Interfaces:**

- Deterministic backend probe sends a safe approval-required result, then a valid approval and replay; it asserts one adapter execution and no secrets in the artifact.
- GUI Harness scenarios agent-terminal-approval and agent-terminal-reject click the named buttons only against fake adapters; they never run a real shell/account/device.

- [ ] **Step 1: Write failing Harness privacy and replay assertions**

Extend tests/test_agent_harness.py to require approval_required, an approval
result, exactly one approved side-effect marker, and absence of arguments,
script, token, secret, and credential from emitted safe events/artifacts. Add a
replay assertion that produces approval_stale.

- [ ] **Step 2: Run the focused Harness tests and verify RED**

Run:

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_agent_harness.py
~~~

Expected: failures only for the new approval/replay assertions.

- [ ] **Step 3: Extend the deterministic fake Broker flow**

Make the fake approval probe emit safe metadata, accept one approval response,
and record only a bounded safe result. Keep raw command fixtures inside the
fake Broker process and out of JSON artifacts. Ensure cleanup removes pending
approval state and the descriptor/ports as before.

- [ ] **Step 4: Extend GUI Harness click scenarios**

Use existing UI Automation IDs to click approve/reject, wait for the terminal
status, then assert the card is hidden and the activity text is bounded. Test
session reset and stale-result behavior without launching PowerShell, Bash, WSL,
web accounts, mail, calendar, microphone, speaker, or VTS.

- [ ] **Step 5: Update operational docs and commit the Harness slice**

Document the control message, one-time expiry/replay guarantees, safe card
fields, and the remaining live acceptance gate in agent-tools.md and
verification.md. Review README only if its commands or feature summary are now
inaccurate. Then run the focused Python/Harness tests and commit:

~~~powershell
git diff --check -- scripts tests gui-winui3.Harness docs/operations README.md
git add -- scripts/aspnetcore_compatibility_harness.py scripts/harness/session.py tests/test_agent_harness.py gui-winui3.Harness/UiAutomationScenario.cs gui-winui3.Harness/Program.cs gui-winui3.Harness/HarnessGuiResult.cs docs/operations/agent-tools.md docs/operations/verification.md README.md
git commit -m "test: cover Agent approval Harness flow"
~~~

## Task 5: Full sequential verification and scoped release

**Files:**

- No new production files. Inspect all feature commits and the existing dirty worktree.

- [ ] **Step 1: Run focused verification in dependency order**

Run one command at a time:

~~~powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --project-root D:\LLM --mode backend-deterministic --timeout-seconds 90 --output D:\LLM\artifacts\aspnetcore\ru-harness-agent-approval.json
dotnet list D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj package --vulnerable --include-transitive
cmd /c D:\LLM\build.bat
~~~

Expected: all tests pass, route matrix remains 41 HTTP + 2 WebSocket, the
deterministic artifact reports approval/replay/privacy/cleanup success, no
vulnerable packages are reported, and self-contained Broker/Backend/GUI
outputs exist.

- [ ] **Step 2: Run privacy and stale-reference scans**

Review every match, with feature-focused scans first:

~~~powershell
rg -n -i "arguments|script|token|secret|credential|approval_id" backend-dotnet/RuAgent.Broker backend-dotnet/RuBackend gui-winui3 scripts tests gui-winui3.Harness --glob '!**/bin/**' --glob '!**/obj/**'
git diff --check -- backend-dotnet/RuAgent.Broker backend-dotnet/RuBackend gui-winui3 scripts tests gui-winui3.Harness docs/operations README.md
~~~

Expected: sensitive terms appear only in validators/tests/docs/private approval
records, never in visible chat, Harness safe events, or logs.

- [ ] **Step 3: Perform user-owned manual acceptance**

The user manually launches the built app and verifies: approval card layout in
light/dark themes, keyboard focus, approve/reject behavior, expiry, session
reset, one-time replay rejection, safe terminal.run, and absence of raw
arguments in chat/history. Real shells, web accounts, connectors, microphone,
speaker, OmniVoice, and VTS remain separate manual gates.

- [ ] **Step 4: Verify scope, commit plan/spec if needed, and push**

Run:

~~~powershell
git status --short --branch
git diff --stat
git diff --cached --name-only
git rev-list --left-right --count main...origin/main
~~~

Stage only approval-flow files. Preserve the known unrelated dirty paths and
the generated artifacts/ directory. Push main only after all automated gates
pass and the user has reviewed the final scope.

## Plan self-review

- Spec coverage: Broker state/protocol is Task 1; Host bridge is Task 2; UI is Task 3; deterministic/manual verification and docs are Task 4; release gates are Task 5.
- Type consistency: the same opaque approval_id is used by Broker, Host, WebSocket, and WinUI; raw arguments remain private to the Broker.
- Privacy coverage: safe metadata is explicitly enumerated, bounded, and tested before any UI code is added.
- Scope safety: no task stages the existing voice/meteor/cleanup changes, and no task enables unrestricted shell or external side effects by default.
- No placeholders or unassigned acceptance requirements remain.
