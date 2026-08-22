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

## Verification

Run the heavyweight checks sequentially on Windows:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_worker_agent_protocol.py tests/test_tool_calls.py tests/test_agent_harness.py
& D:\LLM\.venv\Scripts\python.exe scripts/aspnetcore_compatibility_harness.py --mode backend-deterministic --output D:\LLM\artifacts\aspnetcore\ru-harness-vnext.json
```

The deterministic backend artifact must include `agent.synthetic_tool_loop` and
`agent.private_metadata` as passed, and must report Broker startup and cleanup
as passed. GUI Agent scenarios remain a separate interactive UI gate because
they require launching the WinUI process; they must not start real shells,
web accounts, mail, calendar, microphone, speaker, or VTube Studio devices.

Live terminal behavior, external accounts, and hardware remain explicit manual
acceptance gates after the deterministic checks.
