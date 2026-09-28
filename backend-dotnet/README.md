# Ru ASP.NET Core backend

`RuBackend` is the only public backend for Ru. It binds to loopback port `8000`
when started as `RuBackend.exe --managed-runtime` and owns all 42 HTTP routes
(21 non-Agent routes plus 21 additive
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
one-step, `/agent/v1/*` 21 routes, SSE reconnect with durable `id:<sequence>`
and `Last-Event-ID`/`after_sequence` cursors, durable Core + SQLite
`agent_harness.db` + AES-GCM payloads, workspace trust, session permissions,
and encrypted policy snapshots) is the current path. Worker v2 performs
exactly one `generate_reply` per `agent.step.start` and returns provider-neutral
`AssistantStep(text, tool_calls, finish_reason)`. Its model-visible surface is
the same twelve tools in the single Agent profile: `read`, `list`, `grep`, `glob`,
`help`, `todo_write` and `skill` (one purpose each; `read` takes `offset`/`limit`,
`skill` takes `name` and loads one SKILL.md body),
bounded `truncated`),
`write` with `op=write|delete|restore` plus `edit` (hash-guarded, one-time recoverable,
`recovery_too_large` beyond 1 MiB,
`recovery_consumed` on replay), and `bash` with `mode=run|shell`, plus standalone
`web_search` (`query`, optional `max_results` 1–10) and `web_fetch` (`url`) backed by
the configured Google HTML engine raced against fixed free fallbacks (DuckDuckGo HTML, Bing, Mojeek). Agent runs use the immutable `RuAgent.Protocol` profile catalog. `plan` allows
workspace read and Web while denying terminal and mutation. `standard`
auto-allows bounded-local in-workspace mutation and asks for external/escape or
risky terminal intent. `full_access` auto-allows external structured files and
arbitrary terminal while structured protected paths and hard-invalid requests
remain denied. Repository cleanliness and prompt text never change this matrix.

Workspace trust is explicit once per canonical `WorkspaceIdentity`, independent
of the session-scoped preset. The Host requires a challenge-bound decision; a
moved or recreated identity is untrusted again. The GUI presents a guarded
confirmation and CLI `run`/`chat` use `--trust-workspace`. Full Access requires
the GUI gate or CLI `--confirm-full-access`.

Standard external structured access can create a session-scoped canonical
folder grant for read/list/glob/grep/write/edit; terminal intent never consumes
that grant. Full Access uses an ephemeral per-call external structured root and
does not persist a reusable folder grant. Canonical/symlink/junction/hard-link
checks and structured protected-path denial remain active.

Full-access terminal runs as the current Windows user, can read or change data
outside the workspace, and can open sockets. It is not an OS-enforced
filesystem/network sandbox. Hard timeout, output cap, cancellation, redaction,
and process-tree cleanup still apply. Web is automatic in all canonical
profiles; `web_approval_required` remains available only to an explicit ask
rule. `files.apply_patch` is not advertised or model-usable. The managed Git
workflow retains temporary-index isolation, path rollback attribution, and
refusal on dirty user index state.

The retired Python deterministic Harness is not used for route verification,
so managed route tests run directly against the published C# composition. The
managed composition binds the Agent step route to the C# Worker v2 handler and
serves `GET /status`/`GET /models` directly from the host settings store and the
provider catalog; the media adapters (voice, STT, VTS, observation) and the raw
route fallback no longer exist.

## Session interchange

`RuAgent.SessionInterchange` reads and writes other coding agents' session files, so a
conversation can move between agents instead of living only where it started. It is pure
text in, text out: the adapters never touch the filesystem, because where a file goes
differs per partner (Claude Code keys a directory by project path, Codex keys one by date)
and because an adapter that picked its own destination could not be tested.

Shipped today: **Claude Code**
(`~/.claude/projects/<slugified cwd>/<sessionId>.jsonl`) and **Codex**
(`~/.codex/sessions/<yyyy>/<MM>/<dd>/rollout-*.jsonl`, plus the `session_index.jsonl` line
its thread picker reads), both directions; **Antigravity** and **opencode**, read-only.

Read-only is a decision with a reason for each. Antigravity keeps conversations as protobuf
payloads inside a per-conversation SQLite database whose schema is not on disk, so Ru can
parse its JSONL log but cannot produce a conversation the app would open. opencode moved its
store into SQLite in 1.18, and writing there would mean inserting into its schema with its
own bookkeeping; Ru will not do that to a live projects database without having verified the
shape against a real install, because a wrong insert is not a failed import, it is a damaged
store.

A session's title is derived on read rather than carried: Claude Code has no field for one,
and Codex keeps its copy in the sibling index.

Every format here was copied from real files on disk, and each adapter's doc comment says
which parts are observed and which are inferred. The one inferred piece is Claude Code's
tool blocks: the only transcript available on the machine that wrote the adapter had died
at authentication, before any tool ran.

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
