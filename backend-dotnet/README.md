# Ru ASP.NET Core backend

`RuBackend` is the only public backend for Ru. It binds to loopback port `8000`
when started as `RuBackend.exe --managed-runtime` and owns all 56 HTTP routes
plus the single `/voice/stream` WebSocket (41 legacy routes plus 15 additive
`/agent/v1/*`). Text turns use the current Worker v2-backed Agent HTTP/SSE
contract; no Chat WebSocket is exposed. The legacy
`RuBackend:AgentHarness:Version` setting remains accepted by compatibility
Harness construction, but it is not a product mode switch and cannot re-enable
Chat.

```text
RuApp / RuAgent.Cli (thin client via RuAgent.Client start-or-attach)
        |
        | bearer token or authenticated WebSocket / SSE
        v
ASP.NET Core Public Host :8000 (RuBackend.exe --managed-runtime)
        |\
        | \-- owned Worker/Broker in one kill-on-close Job Object
        |      |-- Python runtime worker :8005 (ru_runtime.worker.server multiplex: /worker/v1 runtime/voice/VTS, /worker/v2 one-step AssistantStep)
        |      |     +-- hosted / external OpenAI-compatible provider adapters
        |      |     +-- OmniVoice and Google STT
        |      |     +-- VTube Studio and observation runtime
        |      `-- private RuAgentBroker (leased loopback endpoint, profile-scoped file/terminal tools)
        |
        | durable Harness: Core + SQLite (agent_harness.db) + encrypted payloads + idempotent history
Public Host owns Worker/Broker in one Job Object; RuApp/CLI only own the
Public Host descriptor lease and long-lived WebSocket/SSE client leases.
```

There is no public Python HTTP server, forwarding catch-all, WebSocket relay,
host-mode selector, or automatic fallback. Git commit `e023fd3` is the final
pre-decommission rollback checkpoint if repository-level recovery is required.

## Ownership

ASP.NET Core directly owns authentication, request-size boundaries, settings,
credentials, sessions, history, activity, structured memory, Agent run ordering,
metadata filtering, and every public endpoint contract. Runtime-dependent work
uses the authenticated `ws://127.0.0.1:8005/worker/v1` protocol:

- runtime transitions (Agent model steps use the typed `/worker/v2` adapter);
- TTS, streaming voice, STT, and GPU lifecycle calls;
- VTube Studio connection, motion, catalog, action, and overlay state;
- structured observation reactions and model/provider status.

The worker is private implementation code, not another public framework. WAV
payloads stay binary instead of being expanded into base64. Agent replies, TTS, history,
memory, activity, and logs keep private voice/VTS metadata out of visible and
persistent text.

## Configuration

| Setting | Default | Purpose |
| --- | --- | --- |
| `RU_DESKTOP_TOKEN` | required per run | Authenticates public REST/WebSocket clients. |
| `RU_WORKER_TOKEN` | required per run | Authenticates ASP.NET to the private worker. |
| `RU_BACKEND_WORKER_PORT` | `8005` | Private worker loopback port selected by RuApp. |
| `RuBackend__BindUrl` | `http://127.0.0.1:8010` standalone | Public ASP.NET bind URL; RuApp sets port `8000`. |
| `RuBackend__WorkerUrl` | `ws://127.0.0.1:8005/worker/v1` standalone | Private worker protocol URL; RuApp overrides it. |
| `RU_DATA_ROOT` | project/user data default | Isolates settings, sessions, activity, and memory for tests. |

Public Host (`RuBackend.exe --managed-runtime`) generates distinct 256-bit
desktop and worker secrets, starts the private Worker (shared
`ru_runtime.worker.server`) and RuAgentBroker before serving public traffic,
assigns Worker/Broker to one kill-on-close Windows Job Object, publishes an
ACL-restricted loopback descriptor for `RuAgent.Client` start-or-attach,
probes public readiness, and terminates the owned Worker/Broker tree on
shutdown or failure. `RuApp` and `RuAgent.Cli` never start Worker/Broker
directly; they validate the descriptor and `GET /status` bearer, and poll for a
valid descriptor if stale/absent. `RuBackend:AgentHarness:Version` defaults to
`v1`; the setting remains accepted by compatibility Harness construction but is
not a product mode switch and cannot re-enable Chat. Agent v2 (`/worker/v2`
one-step, `/agent/v1/*` 15 routes, SSE reconnect with durable `id:<sequence>`
and `Last-Event-ID`/`after_sequence` cursors, durable Core + SQLite
`agent_harness.db` + AES-GCM payloads, workspace trust, session permissions,
and encrypted policy snapshots) is the current path. Worker v2 performs
exactly one `generate_reply` per `agent.step.start` and returns provider-neutral
`AssistantStep(text, tool_calls, finish_reason)`. Its model-visible surface is
the same ten bindings in Standard and Full Access: `files.list`/`files.glob`/
`files.search`/`files.read` (offset/limit, streaming, bounded `truncated`),
hash-guarded `files.write`/`files.edit`, recoverable
`files.delete`/`files.restore` (one-time, `recovery_too_large` beyond 1 MiB,
`recovery_consumed` on replay), and `terminal.run`/`terminal.shell`. Standard
automatically allows ordinary trusted-workspace file reads/creates/edits and
applies analyzer/rule/approval decisions to terminal requests. Full Access
keeps the same advertisement but normally uses its broader fallback for
well-formed calls; protected paths, hard invariants, and extended timeout
thresholds remain gated. An explicit absolute path outside the trusted root
asks with `external_directory_required`; the approval UI can grant only that
canonical directory for the current run or project, and the grant covers
read/create/edit operations only. Terminal calls run as the current Windows
user and may execute host code or open sockets; `network_intent` is
audit/approval metadata, not network isolation. `system.volume` is a
deterministic direct-action path with same-endpoint readback, not a model
binding. `notes.create`, read-only `system.*`, and `app.list` remain
Broker-internal. `files.apply_patch` is not advertised or model-usable. The
managed Git commit workflow (Phase 3 Task 6) is not shipped; Git commands
remain ordinary terminal requests.

## Build and verification

```powershell
dotnet restore backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
dotnet list backend-dotnet/RuBackend/RuBackend.csproj package --vulnerable --include-transitive
& D:\LLM\.venv\Scripts\python.exe scripts\check_aspnetcore_route_matrix.py
& D:\LLM\build.bat
```

The canonical public surface is
[`public-route-contract.json`](public-route-contract.json). The Python checker
validates its schema/source ownership, and `PublicRouteContractTests` compares
it with ASP.NET's live `EndpointDataSource` so a missing route cannot be hidden
by a forwarder.

For an isolated live gate on temporary ports and data:

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\aspnetcore_compatibility_harness.py `
  --worker-port 18030 --edge-port 18031 --require-voice-audio `
  --benchmark-samples 20
```

The harness records only statuses, timings, event types, counts, cleanup flags,
and privacy checks. It must not store tokens, prompts, replies, transcripts,
audio bytes, session IDs, or VTS metadata.

Removing the HTTP proxy eliminates its measured local hop, but it does not make
model inference, cloud providers, voice synthesis, STT, or VTube Studio itself
intrinsically faster. Measure those paths independently before making latency
claims.
