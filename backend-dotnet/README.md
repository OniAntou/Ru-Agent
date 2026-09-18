# Ru ASP.NET Core backend

`RuBackend` is the only public backend for Ru. It binds to loopback port `8000`
when started as `RuBackend.exe --managed-runtime` and owns all 37 HTTP routes
(17 non-Agent routes plus 20 additive
`/agent/v1/*`). Text turns use the current Worker v2-backed Agent HTTP/SSE
contract; no Chat WebSocket is exposed. The legacy
`RuBackend:AgentHarness:Version` setting remains accepted by compatibility
Harness construction, but it is not a product mode switch and cannot re-enable
Chat.

```text
RuApp / RuAgent.Cli (thin client via RuAgent.Client start-or-attach)
        |
        | bearer token or authenticated SSE
        v
ASP.NET Core Public Host :8000 (RuBackend.exe --managed-runtime)
        |\
        | \-- owned managed Worker and Broker in one kill-on-close Job Object
        |      |-- C# Worker process
        |      |     `-- `/worker/v2` :8005 (managed provider adapter, one-step AssistantStep, single-use DPAPI bootstrap)
        |      `-- private RuAgentBroker (leased loopback endpoint, profile-scoped file/terminal tools)
        |
        | durable Harness: Core + SQLite (agent_harness.db) + encrypted payloads + idempotent history
Public Host owns Worker/Broker in one Job Object; RuApp/CLI only own the
Public Host descriptor lease and long-lived SSE client leases.
```

There is no public Python HTTP server, forwarding catch-all, WebSocket relay, or
automatic fallback. Managed startup is unconditional; the retired runtime
selector and its diagnostic were hard-deleted. The current Harness uses a
dependency-free test fixture. Git commit
`e023fd3` remains a historical recovery checkpoint, not a supported runtime.

## Ownership

ASP.NET Core directly owns authentication, request-size boundaries, settings,
credentials, sessions and their message history, activity, Agent run ordering,
metadata filtering, and every public endpoint contract. Agent generation uses
the authenticated managed C# `ws://127.0.0.1:8005/worker/v2` protocol.

Session deletion uses the stable `DELETE /sessions/{sessionId}` route and an
application envelope. Only `status: "ok"` means that the deletion coordinator
removed the session and its owned Harness artifacts; missing or failed deletes
return a bounded `status: "error"` envelope even when the HTTP status is 200.

- runtime transitions (Agent model steps use the typed C# `/worker/v2` adapter);
- `GET /status` and `GET /models` served directly from the host settings store
  and the provider catalog.

The worker is private implementation code, not another public framework. Agent
replies, history,
activity, and logs keep private marker metadata out of visible and
persistent text.

## Configuration

| Setting | Default | Purpose |
| --- | --- | --- |
| `RU_DESKTOP_TOKEN` | required per run | Authenticates public REST clients. |
| `RU_AGENT_WORKER_TOKEN` | generated per managed run | Authenticates ASP.NET to the C# Agent Worker v2. |
| `RU_BACKEND_AGENT_WORKER_PORT` | `8005` | Private Agent Worker loopback port. |
| `RuBackend__BindUrl` | `http://127.0.0.1:8010` standalone | Public ASP.NET bind URL; RuApp sets port `8000`. |
| `RuBackend__AgentWorkerUrl` | `ws://127.0.0.1:8005/worker/v2` standalone | Private Agent Worker protocol URL. |
| `RU_DATA_ROOT` | project/user data default | Isolates settings, sessions, and activity for tests. |

Public Host (`RuBackend.exe --managed-runtime`) generates distinct 256-bit
desktop and Agent Worker secrets, writes a single-use
DPAPI-protected provider bootstrap for the managed C# Worker, starts one C#
Worker serving v2 plus RuAgentBroker before serving public traffic,
assigns all owned children to one kill-on-close Windows Job Object, publishes an
ACL-restricted loopback descriptor for
`RuAgent.Client` start-or-attach,
probes public readiness, and terminates the owned Worker/Broker tree on
shutdown or failure. `RuApp` and `RuAgent.Cli` never start Worker/Broker
directly; they validate the descriptor and `GET /status` bearer, and poll for a
valid descriptor if stale/absent. `RuBackend:AgentHarness:Version` defaults to
`v1`; the setting remains accepted by compatibility Harness construction but is
not a product mode switch and cannot re-enable Chat. Agent v2 (`/worker/v2`
one-step, `/agent/v1/*` 20 routes, SSE reconnect with durable `id:<sequence>`
and `Last-Event-ID`/`after_sequence` cursors, durable Core + SQLite
`agent_harness.db` + AES-GCM payloads, workspace trust, session permissions,
and encrypted policy snapshots) is the current path. Worker v2 performs
exactly one `generate_reply` per `agent.step.start` and returns provider-neutral
`AssistantStep(text, tool_calls, finish_reason)`. Its model-visible surface is
the same eleven tools in the single Agent profile: `read`, `list`, `grep`, `glob`,
`help` and `todo_write` (one purpose each; `read` takes `offset`/`limit`),
bounded `truncated`),
`write` with `op=write|delete|restore` plus `edit` (hash-guarded, one-time recoverable,
`recovery_too_large` beyond 1 MiB,
`recovery_consumed` on replay), and `bash` with `mode=run|shell`, plus standalone
`web_search` (`query`, optional `max_results` 1–10) and `web_fetch` (`url`) backed by
Google HTML with DuckDuckGo fallback. The `standard`
profile automatically allows ordinary trusted-workspace file reads/creates/edits
and applies single-verdict analyzer/rule/approval decisions to terminal requests:
auto for recognized read/test/build/lint/local-Git `bash` with `mode=run` payloads,
ask for deletion, external mutation, extended timeouts, every `bash`
with `mode=shell`
script, and unknown executables. The opt-in `trusted` preset keeps that policy
but auto-allows the in-workspace terminal/external-directory/delete/unknown-executable
subset on a clean workspace (`trusted_workspace_allow`); web, timeouts, rule asks,
invariants, and protected paths stay gated. Protected paths, hard invariants, and extended
timeout thresholds remain gated. An explicit absolute path outside the trusted
root
asks with `external_directory_required`; the approval UI can grant only that
canonical directory for the current live session, and the grant covers
read/create/edit operations only. Managed web tools ask with
`web_approval_required` when approval-gating is enabled. Terminal calls run as
the current Windows user and may execute host code or open sockets;
`network_intent` is
audit/approval metadata, not network isolation. Time, machine status, app
discovery, and optional volume commands use the terminal; notes use
`write` with `op=write`. `files.apply_patch` is not advertised or model-usable. The
managed Git commit workflow is fully shipped with temporary index isolation,
path rollback attribution, and refusal on dirty user state; other Git commands
remain ordinary terminal requests governed by Broker policy.

The retired Python deterministic Harness is not used for route verification,
so managed route tests run directly against the published C# composition. The
managed composition binds the Agent step route to the C# Worker v2 handler and
serves `GET /status`/`GET /models` directly from the host settings store and the
provider catalog; the media adapters (voice, STT, VTS, observation) and the raw
route fallback no longer exist.

## Build and verification

```powershell
dotnet restore backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
dotnet list backend-dotnet/RuBackend/RuBackend.csproj package --vulnerable --include-transitive
.venv\Scripts\python.exe scripts\check_aspnetcore_route_matrix.py
.\build.bat
```

The canonical public surface is
[`public-route-contract.json`](public-route-contract.json). The Python checker
validates its schema/source ownership, and `PublicRouteContractTests` compares
it with ASP.NET's live `EndpointDataSource` so a missing route cannot be hidden
by a forwarder.

The retired Python deterministic Harness is no longer a supported contract
gate. Use the managed .NET test suites and `build.bat`.

Removing the HTTP proxy eliminates its measured local hop, but it does not make
model inference or cloud providers themselves
intrinsically faster. Measure those paths independently before making latency
claims.
