# Ru ASP.NET Core backend

`RuBackend` is the only public backend for Ru. It binds to loopback port `8000`
when started as `RuBackend.exe --managed-runtime` and owns all 63 HTTP routes
plus the single `/voice/stream` WebSocket (42 non-Agent routes plus 20 additive
`/agent/v1/*` plus 1 `POST /host/shutdown`). Text turns use the current Worker v2-backed Agent HTTP/SSE
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
        | \-- owned managed Worker and Broker in one kill-on-close Job Object
        |      |-- C# Worker process
        |      |     |-- `/worker/v2` :8005 (managed provider adapter, one-step AssistantStep)
        |      |     |-- `/worker/v1` :8006 (runtime/voice/STT/VTS/observation)
        |      |     `-- single-use DPAPI provider bootstrap (v2 only)
        |      `-- private RuAgentBroker (leased loopback endpoint, profile-scoped file/terminal tools)
        |
        | durable Harness: Core + SQLite (agent_harness.db) + encrypted payloads + idempotent history
Public Host owns Worker/Broker in one Job Object; RuApp/CLI only own the
Public Host descriptor lease and long-lived WebSocket/SSE client leases.
```

There is no public Python HTTP server, forwarding catch-all, WebSocket relay, or
automatic fallback. Managed startup is unconditional; the retired runtime
selector and its diagnostic were hard-deleted. The current Harness uses a
dependency-free test fixture. Git commit
`e023fd3` remains a historical recovery checkpoint, not a supported runtime.

## Ownership

ASP.NET Core directly owns authentication, request-size boundaries, settings,
credentials, sessions and their message history, activity, structured memory, Agent run ordering,
metadata filtering, and every public endpoint contract. Agent generation uses
the authenticated managed C# `ws://127.0.0.1:8005/worker/v2` protocol; runtime/device
work uses the same managed process at `ws://127.0.0.1:8006/worker/v1`:

Session deletion uses the stable `DELETE /sessions/{sessionId}` route and an
application envelope. Only `status: "ok"` means that the deletion coordinator
removed the session and its owned Harness artifacts; missing or failed deletes
return a bounded `status: "error"` envelope even when the HTTP status is 200.

- runtime transitions (Agent model steps use the typed C# `/worker/v2` adapter);
- TTS, streaming voice, STT, and GPU lifecycle calls;
- VTube Studio connection, motion, catalog, action, and overlay state;
- structured observation reactions and model/provider status.

The workers are private implementation code, not another public framework. WAV
payloads stay binary instead of being expanded into base64. Agent replies, TTS, history,
memory, activity, and logs keep private voice/VTS metadata out of visible and
persistent text.

## Configuration

| Setting | Default | Purpose |
| --- | --- | --- |
| `RU_DESKTOP_TOKEN` | required per run | Authenticates public REST/WebSocket clients. |
| `RU_AGENT_WORKER_TOKEN` | generated per managed run | Authenticates ASP.NET to the C# Agent Worker v2. |
| `RU_DEVICE_WORKER_TOKEN` | generated per managed run | Authenticates ASP.NET to the managed Worker v1 device endpoint. |
| `RU_BACKEND_AGENT_WORKER_PORT` | `8005` | Private Agent Worker loopback port. |
| `RU_BACKEND_DEVICE_WORKER_PORT` | `8006` | Private Device Worker loopback port. |
| `RuBackend__BindUrl` | `http://127.0.0.1:8010` standalone | Public ASP.NET bind URL; RuApp sets port `8000`. |
| `RuBackend__AgentWorkerUrl` | `ws://127.0.0.1:8005/worker/v2` standalone | Private Agent Worker protocol URL. |
| `RuBackend__DeviceWorkerUrl` | `ws://127.0.0.1:8006/worker/v1` standalone | Private Device Worker protocol URL. |
| `RU_DATA_ROOT` | project/user data default | Isolates settings, sessions, activity, and memory for tests. |

Public Host (`RuBackend.exe --managed-runtime`) generates distinct 256-bit
desktop, Agent Worker, and Device Worker secrets, writes a single-use
DPAPI-protected provider bootstrap for the managed C# Worker, starts one C#
Worker serving v2 and v1 plus RuAgentBroker before serving public traffic,
assigns all owned children to one kill-on-close Windows Job Object, publishes an
ACL-restricted loopback descriptor for
`RuAgent.Client` start-or-attach,
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
the same eleven bindings in Standard and Full Access (including offline
`help.docs`): `files.list`/`files.glob`/
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
audit/approval metadata, not network isolation. Time, machine status, app
discovery, and optional volume commands use the terminal; notes use
`files.write`. `files.apply_patch` is not advertised or model-usable. The
managed Git commit workflow (Phase 3 Task 6) is not shipped; Git commands
remain ordinary terminal requests.

The retired Python deterministic Harness is not used for route verification,
so managed route tests run directly against the published C# composition. The managed composition now
binds every allowlisted private route to an explicit C# handler: status/models
report the active typed settings (the worker never receives provider
credentials), VTS owns runtime-scoped GUI state/config plus
catalog/action/connect handling, and persists its authentication token through
the canonical `pyvts_token.txt` path, voice
and STT use their typed adapters, and observation status/control returns a
bounded disabled/unavailable result until a managed screen-capture/reaction
loop is shipped. The raw route fallback is no longer reachable from the
managed composition; enabling the optional overlay is fail-closed. The
provider-backed model catalog, VTS overlay process/semantic actions, and
observation capture/reaction still need a
dedicated managed-runtime integration gate before claiming full production
parity.

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

The retired Python deterministic Harness is no longer a supported contract
gate. Use the managed .NET test suites and `build.bat`; native voice and VTube
acceptance remain separate user-run gates.

Removing the HTTP proxy eliminates its measured local hop, but it does not make
model inference, cloud providers, voice synthesis, STT, or VTube Studio itself
intrinsically faster. Measure those paths independently before making latency
claims.
