# Ru current architecture

Status: current ownership plus accepted architecture-v2 targets, 2026-08-29.

Operational invariants for generation, voice, STT, VTS, observation, and live
acceptance are maintained in
[`operations/runtime-behavior.md`](operations/runtime-behavior.md).

## Process topology

Public Host (`RuBackend.exe --managed-runtime`, ASP.NET Core) owns the
Worker/Broker lifecycle in one Windows kill-on-close Job Object. `RuApp` (WinUI)
and the headless `RuAgent.Cli` start-or-attach the Public Host through the
shared `RuAgent.Client` (`PublicHostStarter`/`PublicHostLocator` with
ACL-restricted loopback descriptor and bearer probe). On first start the Public
Host creates per-run desktop and worker secrets, starts the private Runtime
Worker on loopback port 8005 via the shared `ru_runtime.worker.server`
multiplexer (`/worker/v1` runtime/voice/VTS/observation, `/worker/v2` one-step Agent), starts the
isolated `RuAgentBroker` on a dynamic loopback port, binds ASP.NET Core on port
8000, waits for public readiness, and assigns Worker/Broker to one Job Object.
Idle Public Host exits after 10 minutes without an active Agent run, approval,
or long-lived Agent SSE lease. All three owned children still
enter one Job Object, but ownership is Public Host, not WinUI. Startup succeeds
only after public readiness; shutdown or startup failure terminates the owned
Worker/Broker tree.

The current process flow (Phase 1 headless harness foundation) is:

```text
RuApp / RuAgent.Cli  --start-or-attach via RuAgent.Client-->  ASP.NET Core Public Host :8000
                                                                (RuBackend.exe --managed-runtime, owns one kill-on-close Job Object)
                                                                  |-- direct business, persistence, and durable Harness (Core + SQLite agent_harness.db + encrypted payloads)
                                                                  |-- private call to Runtime Worker :8005 (ru_runtime.worker.server: /worker/v1 + /worker/v2 one-step AssistantStep)
                                                                         `-- private call to RuAgentBroker execution boundary (profile-scoped file/terminal tools)
                                                                Worker/Broker owned children:
                                                                  |-- Runtime Worker :8005
                                                                  |     |-- hosted and external OpenAI-compatible provider adapters
                                                                  |     |-- Google STT, VTube Studio, and observation implementations
                                                                  |     `-- OmniVoice Flask service :5052 in omnivoice_env
                                                                  `-- RuAgentBroker
                                                                        `-- policy, approvals, and local action adapters
```

Additional harness surfaces (additive, feature-flagged `RuBackend:AgentHarness:Version=v1` default; durable policy/file-tool slice complete, with headless terminal and reconciliation surfaces from Phase 3):

- Worker v2: one `agent.step.start` → exactly one `generate_reply` → `AssistantStep(text, tool_calls, finish_reason)`, with braced JSON never streaming as delta and provider errors mapped to bounded `worker.*` codes.
- Agent API/SSE: 13 `/agent/v1/*` routes plus durable `HarnessEvent` SSE (`id:<sequence>`, `Last-Event-ID`/`after_sequence` cursors, reconnect replay, transient non-durable deltas) with bearer auth, idempotency 409, `workspace_busy` 409, `preset_mismatch` 409, active `GET`/`POST`/`DELETE /agent/v1/policies` (global requires `X-Interactive-Confirmed: true`, versioned JSON, encrypted matcher values) and `POST /agent/v1/runs/{runId}/approvals/{approvalId}` (first-writer-wins `approval_stale`, exact scope preview, `approval_cannot_create_global`, 30-min expiry, old-snapshot execution ordering); `reconcile` is active on the Agent v2 headless path (Phase 3).
- Broker v2: immutable `PolicySnapshot` (version, preset, workspace identity `VolumeSerial`/`RootFileId`/`DisplayHash` `ws_*`, ordered `PolicyRule`/`PolicyMatcher` with tool/path/executable/argv/cwd/operation/network, SHA-256 canonical JSON), `BrokerRunLease` binding and hash validation, `PolicyContext`/`PolicySnapshotEvaluator` authoritative (invariants first, last-match wins, Standard `allow` for ordinary reads/creates/edits and `ask` for delete/protected/outside/network/unknown, Full `allow` fallback never bypassing trust), explicit absolute external paths with run-scoped directory grants and host-generated project descendant rules, `CanonicalPathResolver`/`ProtectedPathCatalog` (DB/key/descriptor/rule locations, credentials, `.git/**`, `.env*`, version `1`), and approval `PendingApprovalStore`/`ApprovalTokenService` (30-min wait, 60-s token, `approval_stale` on replay, old-snapshot execution ordering, `reusable_rule_not_saved` still permits once-only execution).
- File tools: `files.list`/`files.glob`/`files.search`/`files.read` (streaming enumeration, ordinal relative paths, hidden/binary/protected skip with counts, result/byte/time caps, offset beyond EOF, UTF-8 boundary, bounded `truncated` metadata, junction/symlink containment, no unbounded allocation) plus hash-guarded `files.write`/`files.edit` (required `expected_sha256` `missing`/64-hex, `workspace_changed` on mismatch, same-volume temp + flush/atomic replacement, 4 MiB cap, UTF-8, exact occurrences, before/after hashes, bounded diff summary, run-attributed change set) and recoverable `files.delete`/`files.restore` (encrypted recovery copy capped 1 MiB, `recovery_too_large`, opaque `recovery_id`, one-time `recovery_consumed`/`recovery_target_exists`, byte-identity, never in diagnostics). `files.apply_patch` remains an internal/non-advertised adapter and is not model-usable.
- Headless Agent v2 terminal: `terminal.run`/`terminal.shell` are policy-evaluated and require explicit approval for every request; they run as the current Windows user and can execute host code or open sockets. The managed Git commit workflow (Phase 3 Task 6) is not shipped; Git commands are not a separate managed capability.
- CLI tracer: thin headless `RuAgent.Cli` delegates start/attach to `RuAgent.Client` and all run behavior to HTTP/SSE; renders only safe envelope fields (NDJSON `v:1` with `--json`, no private payload auto-fetch, no prompt/args/output/secret fields) and now exposes `agent approve --scope once|run|project`/`deny`/`policy list`/`import --global FILE`/`remove` plus `run --preset full` interactive warning (`--json` cannot bypass).

The current Agent flow is:

```text
RuApp -> ASP.NET Core public contract -> typed worker protocol v2 -> Runtime Worker
Runtime Worker -> model adapters / Voice Engine / STT / VTS / observation
```

The typed Worker protocol v2 is the active Agent execution path. The framed
Voice Engine remains an accepted future target, not a claim about the current
Flask-backed implementation. The proposed [OpenCode-style system-prompt
composer](superpowers/specs/2026-08-29-ru-opencode-system-prompt-composer-design.md)
remains a separate follow-up and must not reintroduce a second conversation
transport; it is not implemented in this architecture snapshot yet.

## Harness ownership

The vNext compatibility Harness is a test-only owner. Its CLI creates a
temporary data/log root, three dynamically leased loopback ports, memory-only
bearers, and one Worker, Broker, and ASP.NET Core edge child. While the port
leases are still held, it prebuilds Broker and edge sequentially; both are then
started with `--no-build` so their shared `RuAgent.Protocol` output cannot race
during a cold build. It classifies all 54 HTTP and 1 WebSocket contract entries
(41 legacy plus 13 additive `/agent/v1/*`), uses deterministic fixtures for safe
mutations and voice plus additive Agent v2 tracer fixtures (idempotency,
workspace trust via `WindowsWorkspaceIdentityResolver` final-path/volume/file
identity, policy snapshots, SSE cursor, payload binding, file discovery/read
and hash-guarded mutations/patch/delete/restore with recovery caps, approval
first-writer-wins and snapshot ordering), skips observation routes that require
hardware or external services, and removes the temporary root during idempotent
cleanup. An early child exit is recorded only as its
role, exit code, and lifecycle classification; raw child output is not copied
into the public artifact.
The Harness does not change the Public Host's normal production ownership of
Worker/Broker in its Job Object, nor the start-or-attach `RuAgent.Client`
contract for RuApp/CLI; credentials and fixed runtime defaults remain
Public Host-owned.

The GUI Harness extends that ownership boundary through an explicit
RuAppComposition: production uses the managed backend provider and physical
AudioGraph/MediaPlayer adapters, while a short-lived loopback descriptor selects
an external Harness edge, deterministic PCM frame source, recording playback
device, and bounded JSONL event sink. The WinUI shell, API clients,
Agent SSE client, voice state machine, session history path, and reply lifecycle
remain the same. Harness mode skips subtitle overlay and observation polling,
never takes over an existing RuApp instance, and does not report VTube Studio
as connected.

## Ownership

| Owner | Responsibilities |
| --- | --- |
| RuApp / RuAgent.Cli (via RuAgent.Client) | Desktop views (RuApp) or headless rendering (CLI), playback, microphone capture, public client calls via `AgentApiClient`/`SessionApiClient`/`AgentSseClient`, and user-visible state; start-or-attach Public Host through validated descriptor + `GET /status` bearer, but do not directly own Worker/Broker. |
| ASP.NET Core Public Host (`RuBackend.exe --managed-runtime`) | Authentication, limits, all 54 HTTP and 1 WebSocket route (41 + 13 `/agent/v1/*`), settings transactions, credentials, sessions, history, activity, structured memory, Agent run ordering, public error mapping, metadata filtering, durable Harness (Core state machines incl. `tool.execution_requested`, SQLite WAL + AES-GCM payloads, conversation outbox, workspace trust with valid `WorkspaceIdentity`, session permissions with monotonic version, encrypted policy snapshots `PolicySnapshot`/`PolicyRule`/`PolicyMatcher` with `Sha256` and `PolicySnapshotCanonicalizer` ordering, `FileRecoveryStore`/`RunChangeSetStore`, approval ordering `consume → persist reusable rule → execute under N → receipt → N+1`), Agent SSE (`id:<sequence>` cursors, `cursor_mismatch`, transient `t` IDs), and Worker/Broker child ownership in one kill-on-close Job Object with 10-min idle lease (active run/approval/long-lived lease suppresses exit). |
| Runtime Worker | Provider adapters and device implementations that materially require Python: Agent model steps via v2, STT execution, VTS, observation, and current OmniVoice coordination; reached via shared `ru_runtime.worker.server` (`/worker/v1` runtime/voice/VTS/observation + `/worker/v2` Agent). |
| RuAgentBroker | Private execution boundary for model-visible tools, capability policy, approvals, bounded adapters, and verified action receipts; private `run.open` lease binds the immutable `PolicySnapshot` (preset, workspace identity, ordered rules, SHA-256) and canonical workspace root, validates hash before capabilities, and advertises model-usable `files.*` (`list`/`glob`/`search`/`read`/`write`/`edit`/`delete`/`restore` one-time). Explicit absolute paths outside the root can receive a host-generated canonical-directory grant for the current run or project; that grant is limited to read/create/edit and never bypasses protected/destructive gates. The Broker also exposes headless terminal bindings, `system.volume`, and `notes.create`. `files.apply_patch` is not advertised. It has no public HTTP contract. |
| Voice Engine | OmniVoice model lifecycle and native WAV synthesis inside its isolated environment; currently reached through Flask and replaced by framed process I/O in a later plan. |

Unknown public paths stop in ASP.NET Core. Python has no public HTTP fallback,
forwarding catch-all, route ownership selector, or alternate host mode.

## Contracts and transport

The canonical Public Contract is
[`backend-dotnet/public-route-contract.json`](../backend-dotnet/public-route-contract.json):
54 HTTP method/path pairs and 1 WebSocket route (`/voice/stream`, plus 13
`/agent/v1/*`); REST, WebSocket and SSE clients
authenticate with the desktop secret (bearer for HTTP/SSE). ASP.NET Core maps
every public response and error; `GET /agent/v1/runs/{runId}/events` is SSE
with durable `id: <sequence>` and reconnect via `Last-Event-ID` or
`?after_sequence=` with `cursor_mismatch` rejection.

Worker contracts are versioned typed WebSockets:

- Protocol v1 (`/worker/v1`) is the private runtime/device adapter: `runtime.request`/`runtime.response`, voice, VTS, health, and observation operations remain; it has no generation or memory-extract messages.
- Protocol v2 (`/worker/v2`, additive, feature-flagged) is one-step Agent: `agent.step.start` → `agent.step.delta`* → `agent.step.done`/`error`, with `agent.step.cancel`, bounded schemas, `AssistantStep(text, tool_calls, finish_reason)` (one `generate_reply` per step), and stable `worker.*` error codes; it never accepts `chat.tool.result`.

Protocol v2 removes HTTP-shaped fields in favor of typed domain commands,
capabilities, bounded payloads, ordering, cancellation, and stable worker
errors. `RuAgent.Cli` never speaks Worker protocols directly; it is a thin
HTTP/SSE client.

Public `/voice/stream` audio is carried as binary WAV frames. In the current
implementation the Runtime Worker reaches OmniVoice over loopback HTTP on port
5052. The accepted Voice Engine target uses bounded stdin/stdout control and
binary frames, keeps native-speed audio, and exposes no network listener.

Text turns are authenticated Agent runs consumed through the durable Agent SSE
stream. The only public WebSocket is `/voice/stream`; it carries binary WAV
frames and does not open a second text-generation transport.

## Data ownership

ASP.NET Core owns business persistence. Settings, history, sessions, activity,
and the VTS token default to the canonical `%LOCALAPPDATA%\Ru` location;
structured memory defaults to `data/conversations`, with `RU_DATA_ROOT`
available for isolated runs. Secrets are stored through Windows Credential
Manager rather than JSON files.

The transactional migration from `~\.minigpt` completed on 2026-08-17 with
copy, validation, hashing, atomic activation, readability checks, and
credential migration performed before legacy records were retired; see
[ADR 0004](adr/0004-canonical-ru-data-location.md).

## Privacy boundary

Only data needed for a requested operation crosses a private boundary. Visible
assistant text may enter Agent generation and approved persistence, but transport-only
voice controls and private VTS actions, motions, overlays, timings, segment
identifiers, and model-control metadata must not enter TTS text, history,
memory, reviewed examples, cleanup manifests, or logs.

Audio remains binary and manifests contain metadata only. Automated checks may
record statuses, timings, counts, event types, and cleanup state; they never
record credentials, bearer tokens, request bodies, session identifiers,
transcripts, audio bytes, or private VTS metadata.
