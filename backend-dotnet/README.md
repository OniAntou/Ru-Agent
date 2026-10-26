# Ru ASP.NET Core backend

`RuBackend` is the only public backend for Ru. When started as
`RuBackend.exe --managed-runtime`, it asks Kestrel to bind an available loopback
port and publishes the selected address in its runtime descriptor after bind.
It owns all 49 HTTP routes
(21 non-Agent routes plus 25 additive
`/agent/v1/*`). Text turns use the current in-process-model-step Agent HTTP/SSE
contract; no Chat WebSocket is exposed.

```text
RuApp / RuAgent.Cli (thin client via RuAgent.Client start-or-attach)
        |
        | bearer token or authenticated SSE
        v
ASP.NET Core Public Host 127.0.0.1:<ephemeral> (RuBackend.exe --managed-runtime, one process)
        |\
        | \-- in-process typed model step (ModelStepService behind IModelStepRunner)
        |     `-- bounded provider adapters + one-step AssistantStep
        |
        |-- in-process execution authority (BrokerExecutionRuntime behind IToolGatewayFactory)
        |     |-- policy, approvals, profile-scoped structured file tools
        |     `-- terminal/git children as the current Windows user
        |
        | durable Harness: Core + SQLite (agent_harness.db) + encrypted payloads + idempotent history
```

The Public Host is one process with no managed child; RuApp/CLI only own the
Public Host descriptor lease and long-lived SSE client leases.

There is no public Python HTTP server, forwarding catch-all, WebSocket relay, or
automatic fallback. Managed startup is unconditional; the retired runtime
selector and its diagnostic were hard-deleted. The current Harness uses a
dependency-free test fixture. Git commit
`e023fd3` remains a historical recovery checkpoint, not a supported runtime.

Desktop and CLI release packages each carry the matching Host; an installed
client requires an exact software-version match before attaching. The update
maintenance handshake reuses the existing authenticated shutdown route and
refuses unfinished runs. See [distribution and updates](../docs/operations/distribution-and-updates.md).

## Ownership

ASP.NET Core directly owns authentication, request-size boundaries, settings,
credentials, sessions and their message history, activity, Agent run ordering,
metadata filtering, and every public endpoint contract. Agent generation and
tool execution run in process through the typed `ModelStepService` and the
`BrokerExecutionRuntime` composed behind `IToolGatewayFactory`; the Host starts
no managed child.

Session deletion uses the stable `DELETE /sessions/{sessionId}` route and an
application envelope. Only `status: "ok"` means that the deletion coordinator
removed the session and its owned Harness artifacts; missing or failed deletes
return a bounded `status: "error"` envelope even when the HTTP status is 200.

- runtime transitions (Agent model steps use the typed in-process step service);
- `GET /status` and `GET /models` served directly from the host settings store
  and the provider catalog.

The model runner is private implementation code, not another public framework. Agent
replies, history,
activity, and logs keep private marker metadata out of visible and
persistent text.

## Configuration

| Setting | Default | Purpose |
| --- | --- | --- |
| `RU_DESKTOP_TOKEN` | required per run | Authenticates public REST clients. |
| `RuBackend__BindUrl` | `http://127.0.0.1:8010` standalone | Public ASP.NET bind URL; managed desktop startup uses `http://127.0.0.1:0` unless explicitly configured. |
| `RU_DATA_ROOT` | project/user data default | Isolates settings, sessions, and activity for tests. |
| `RU_MAX_BACKGROUND_TERMINAL_TASKS` | `8` | Per-session running terminal capacity (1–64), including foreground reservations. Invalid explicit values fail initialization; a full session refuses new launches without stopping existing tasks. |

Public Host (`RuBackend.exe --managed-runtime`) generates a 256-bit
desktop secret, binds loopback, then publishes an ACL-restricted descriptor for
`RuAgent.Client` start-or-attach, and probes public readiness before serving.
`RuApp` and `RuAgent.Cli` never start an execution process; they validate the
descriptor and `GET /status` bearer, and poll for a valid descriptor if
stale/absent. Agent v2 (in-process
one-step model generation, `/agent/v1/*` 28 routes, SSE reconnect with durable
`id:<sequence>`
and `Last-Event-ID`/`after_sequence` cursors, durable Core + SQLite
`agent_harness.db` + AES-GCM payloads, workspace trust, session permissions,
and encrypted policy snapshots) is the current path. A model step performs
exactly one provider generation per typed request and returns provider-neutral
`AssistantStep(text, tool_calls, finish_reason)`. Its model-visible surface is
selected by its pinned `coding_v2`, `patch_v2` or `coding_code_v1` inventory.
The [tool contract](../docs/operations/agent-tools.md#coding-profiles) defines
explicit file and Windows-terminal operations, reads/search, Web, questions,
MCP and subagent control. File mutations remain hash-guarded; delete recovery
is bounded and one-time (`recovery_too_large` beyond 4 MiB,
`recovery_consumed` on replay). Terminal output can be paged without rerunning.
The [canonical tool policy](../docs/operations/agent-tools.md#current-capability-matrix)
owns preset decisions and approval reasons; Protocol owns the executable catalog.
Terminal children run as the current Windows user without an OS filesystem or
network sandbox. Hard timeout, output, cancellation and process-tree bounds apply.

Workspace trust is explicit once per canonical `WorkspaceIdentity`, independent
of the session-scoped preset. The Host requires a challenge-bound decision; a
moved or recreated identity is untrusted again, and the GUI re-asks with a
one-click "trust again" prompt that names the change while `--trust-workspace`
re-approves it for scripts. The GUI presents a guarded
confirmation and CLI `run`/`chat` use `--trust-workspace`. Full Access requires
the GUI gate or CLI `--confirm-full-access`.

Standard external structured access can create a session-scoped canonical
folder grant for read/list/glob/grep/write/edit; terminal intent never consumes
that grant. An external-directory approval can also be remembered with
`allow_always`: the Host saves the approved folder as a durable project-scope
path pattern (`<folder>\**`) that covers the same structured tools and survives
restarts and later sessions of the same workspace, and the evaluator honors a
matched allow rule for external targets (deny and ask rules keep precedence).
`allow_always` on a terminal ask remembers a bounded command pattern — the
resolved executable plus at most two leading non-flag argv tokens with the
exact launch directory — while flag-first invocations and shell-mode scripts
stay exact. `rule_asks` and `terminal_protected_path_requires_approval` never
become a reusable rule. Full Access uses an ephemeral per-call external
structured root and does not persist a reusable folder grant.
Canonical/symlink/junction/hard-link checks remain active in every preset, and
the structured protected-path denial stays active in `plan` and `standard`.

Both terminal modes run as the current Windows user, can read or change data
outside the workspace, and can open sockets. There is no OS-enforced
filesystem/network sandbox (ADR 0015); Standard authorizes the canonical
launch directory (ADR 0017). Hard timeout, output cap,
cancellation, redaction, and process-tree cleanup still apply. Web is automatic in all canonical
profiles; `web_approval_required` remains available only to an explicit ask
rule. `files.apply_patch` is not advertised or model-usable. The managed Git
workflow retains temporary-index isolation, path rollback attribution, and
refusal on dirty user index state.

Background terminal tasks are session-scoped and Broker-owned: `background: true`
returns immediately with an `execution_id` and `state=running`, a foreground
command that outlives its approved timeout is moved to the background instead of
being killed, and the `/tasks` command lists the session's tasks or stops one by
id.

The retired Python deterministic Harness is not used for route verification,
so managed route tests run directly against the published C# composition. The
managed composition binds the Agent step route to the in-process model runner
and serves `GET /status`/`GET /models` directly from the host settings store
and the provider catalog; the media adapters (voice, STT, VTS, observation)
and the raw route fallback no longer exist.

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
