# Agent tools and skills

Ru's Agent mode is a session-scoped capability layered on the existing
Public Host and Runtime Worker. The WinUI toggle sends `agent_mode: true` on
the authenticated `/chat` connection. The Worker may emit a structured
`chat.tool.request`; the Host validates the session and forwards the typed
request to the private `RuAgentBroker`.

## Runtime boundary

```text
WinUI Agent session
        -> authenticated Public Host
        -> private Worker structured tool request
        -> RuAgentBroker policy + manifest registry
        -> bounded adapter
        -> typed result back to Worker
```

The desktop starts the Worker, Broker, and Public Host in one owned process
group. The Broker uses a loopback-only descriptor with a per-run bearer and a
short expiry. Its descriptor, session root, grants, and temporary fixtures are
removed during shutdown.

The first usable tool set is deliberately small:

- `files.list` and `files.read` stay inside the configured workspace and have
  bounded results.
- `system.time`, `system.status`, and `app.list` are read-only metadata tools.
- `system.volume@1.0.0` is the first local reversible write tool. It targets
  the Windows default render endpoint through Core Audio, requires the
  session-scoped `system.write` scope, and does not require one-time approval.
  Its bounded operations are `get`, exact `set` (0–100), relative
  `increase`/`decrease` (1–50), `mute`, and `unmute`; every write is successful
  only after readback verification. Normal chat accepts compatibility
  `[VOLUME: ...]` markers, while Agent mode uses a strict JSON tool request and
  a separate natural-language result phase.
- `terminal.run` accepts only the registered non-admin executable allowlist,
  workspace-relative directories, bounded arguments/output, cancellation, and
  process-tree cleanup.
- `terminal.shell`, network-enabled terminal calls, web access, notes, and
  connector side effects are not silently enabled; policy marks the relevant
  external/destructive action for approval or denies it.

Skill packages are declarative `skill.json` plus `instructions.md` files. The
registry validates IDs, semver, named tools/scopes, step count, timeout,
schemas, trust class, and executable-content absence. A skill cannot create a
tool ID dynamically, change its grant, or start a background task.

## Privacy and approval invariants

Agent activity sent to the GUI is a bounded safe summary. Raw arguments,
credentials, transcripts, audio, bearer values, and tool output do not enter
visible chat or Harness artifacts. Web content is wrapped as untrusted data so
instructions inside a fetched page cannot become model instructions.

External and destructive operations require a short-lived, one-time approval
bound to the session, request identity, tool version, and canonical arguments.
Changing the arguments, using another session, replaying the token, or using
an expired approval fails closed.

## One-time approval UI

When a registered Agent operation is classified as external or destructive,
the Broker keeps the original typed request and its canonical arguments hash
in memory. The public Host and WinUI receive only bounded metadata:

- `approval_id`, tool identity, safe action, scope, and expiry;
- no arguments, script, credential, bearer, connector body, or raw adapter output.

WinUI renders that metadata in a compact approval card above the composer. The
card is not a chat message, so it is not written to conversation history or
memory. `Duyệt một lần` and `Từ chối` disable while the decision is in flight;
the card is cleared on a terminal result, session change, Agent disable,
disconnect, expiry, or disposal.

The authenticated `/chat` WebSocket accepts this control envelope and nothing
else for the approval decision:

```json
{
  "type": "agent.approval.response",
  "session_id": "...",
  "approval_id": "...",
  "approved": true
}
```

The Host forwards only `approval_id` and `approved` through the private Broker
WebSocket. The Broker consumes the pending record atomically, rechecks policy,
and dispatches the original typed request at most once. A second response,
wrong session, changed request, expired record, or unavailable adapter returns
a bounded result and performs no uncontrolled side effect. `terminal.run`
remains the structured, allowlisted, approval-free path; raw shell and
external connector paths stay gated. The Worker `chat.tool.result` contract
accepts `approval_required` as a non-terminal tool state so the approval card
can be reached without converting the Agent turn into a protocol error.
The local reversible `system.volume` result is bounded to `operation`,
`level`, `muted`, and `verified`; native errors, device names, arguments, and
model success claims are not forwarded when readback fails.

## Verification

Run the heavyweight checks sequentially on Windows:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_worker_agent_protocol.py tests/test_tool_calls.py tests/test_agent_harness.py
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_volume_control.py tests/test_system_pc_tools.py tests/test_reply_orchestration.py tests/test_runner_status.py tests/test_worker_runtime_handlers.py
& D:\LLM\.venv\Scripts\python.exe scripts/aspnetcore_compatibility_harness.py --mode backend-deterministic --output D:\LLM\artifacts\aspnetcore\ru-harness-vnext.json
```

The deterministic backend artifact must include `agent.synthetic_tool_loop`,
`agent.approval_dispatch`, `agent.approval_replay`, and
`agent.private_metadata` as passed, and must report Broker startup and cleanup
as passed. GUI Agent scenarios remain a separate interactive UI gate because
they require launching the WinUI process; they must not start real shells,
web accounts, mail, calendar, microphone, speaker, or VTube Studio devices.

Live terminal behavior, external accounts, and hardware remain explicit manual
acceptance gates after the deterministic checks.

For the volume tool, automated tests use fake Core Audio controllers and a
fake Broker WebSocket controller; they prove validation, exact/relative
semantics, mute state, scope policy, and readback mismatch handling without
changing the machine. Manual Windows acceptance is still required for the
real default render endpoint: verify `get`, exact `set`, a relative change,
`mute`, `unmute`, and a forced failure/unchanged-state case in both normal chat
and Agent mode. Do not report live audio success from deterministic gates.
