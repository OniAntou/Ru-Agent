# Ru current architecture

Status: current ownership plus accepted architecture-v2 targets, 2026-08-30.

Operational invariants for generation, voice, STT, VTS, observation, and live
acceptance are maintained in
[`operations/runtime-behavior.md`](operations/runtime-behavior.md).

## Process topology

Public Host (`RuBackend.exe --managed-runtime`, ASP.NET Core) owns the
Worker/Broker lifecycle in one Windows kill-on-close Job Object. `RuApp` (WinUI)
and the headless `RuAgent.Cli` start-or-attach the Public Host through the
shared `RuAgent.Client` (`PublicHostStarter`/`PublicHostLocator` with
ACL-restricted loopback descriptor and bearer probe). On first start the Public
Host creates per-run desktop and separate Agent/device worker secrets, starts
one managed C# Worker serving Agent v2 on loopback port 8005 and Device v1
on loopback port 8006, starts the isolated `RuAgentBroker` on a dynamic
loopback port, binds ASP.NET Core on port 8000, waits for public readiness,
and assigns the Worker/Broker to one Job Object. The explicit
`PythonCompatibility` rollback starts the legacy Python Agent and Device
workers instead. Idle Public Host exits after 10 minutes without an active
Agent run, approval, or long-lived Agent SSE lease. Both owned children enter
one Job Object, but ownership is Public Host, not WinUI. Startup succeeds only
after public readiness; shutdown or startup failure terminates the owned
Worker/Broker tree.

The current managed process flow (Phase 2 cutover) is:

```text
RuApp / RuAgent.Cli  --start-or-attach via RuAgent.Client-->  ASP.NET Core Public Host :8000
                                                                (RuBackend.exe --managed-runtime, owns one kill-on-close Job Object)
                                                                  |-- direct business, persistence, and durable Harness (Core + SQLite agent_harness.db + encrypted payloads)
                                                                  |-- private call to managed C# Worker :8005 (Worker v2 one-step AssistantStep)
                                                                  |-- private call to managed C# Worker :8006 (Worker v1 runtime/voice/STT/VTS/observation)
                                                                         `-- private call to RuAgentBroker execution boundary (profile-scoped file/terminal tools)
                                                                Worker/Broker owned children:
                                                                  |-- managed C# Worker :8005/:8006
                                                                  |     |-- bounded OpenAI-compatible provider adapter
                                                                  |     |-- typed runtime, voice, STT, VTube Studio, and observation adapters
                                                                  |     |-- protected single-use provider bootstrap (v2 only)
                                                                  |     `-- isolated OmniVoice Flask service :5052 in omnivoice_env
                                                                  `-- RuAgentBroker
                                                                        `-- policy, approvals, and local action adapters
```

The public Agent path uses the Worker v2 harness. The legacy
`RuBackend:AgentHarness:Version` setting remains accepted by compatibility
Harness construction, but it is not a product mode switch and cannot re-enable
Chat. Durable policy/file-tool work is complete, with headless terminal and
reconciliation surfaces from Phase 3:

- Worker v2: one `agent.step.start` → exactly one `generate_reply` → `AssistantStep(text, tool_calls, finish_reason)`, with braced JSON never streaming as delta and provider errors mapped to bounded `worker.*` codes. The managed C# Agent Worker composes one system message from seven ordered layers using a fresh host-built `SystemPromptContext` and returns a digest-only receipt that Core validates before completion or tool dispatch.
- Agent API/SSE: 15 `/agent/v1/*` routes plus durable `HarnessEvent` SSE (`id:<sequence>`, `Last-Event-ID`/`after_sequence` cursors, reconnect replay, transient non-durable deltas) with bearer auth, idempotency 409, `workspace_busy` 409, `preset_mismatch` 409, typed command dispatch (`/compact`, `/help`, `/skills`, `/skill`) without history insertion, active `GET`/`POST`/`DELETE /agent/v1/policies` (global requires `X-Interactive-Confirmed: true`, versioned JSON, encrypted matcher values) and `POST /agent/v1/runs/{runId}/approvals/{approvalId}` (first-writer-wins `approval_stale`, exact scope preview, `approval_cannot_create_global`, 30-min expiry, old-snapshot execution ordering); `reconcile` is active on the Agent v2 headless path (Phase 3).
- Broker v2: immutable `PolicySnapshot` (version, preset, workspace identity `VolumeSerial`/`RootFileId`/`DisplayHash` `ws_*`, ordered `PolicyRule`/`PolicyMatcher` with tool/path/executable/argv/cwd/operation/network, SHA-256 canonical JSON), `BrokerRunLease` binding and hash validation, `PolicyContext`/`PolicySnapshotEvaluator` authoritative (invariants first, last-match wins, Standard `allow` for ordinary reads/creates/edits and analyzer/rule-based terminal decisions, Full `allow` fallback for well-formed calls, neither bypassing trust or hard invariants), explicit absolute external paths with run-scoped directory grants and host-generated project descendant rules, `CanonicalPathResolver`/`ProtectedPathCatalog` (DB/key/descriptor/rule locations, credentials, `.git/**`, `.env*`, version `1`), and approval `PendingApprovalStore`/`ApprovalTokenService` (30-min wait, 60-s token, `approval_stale` on replay, old-snapshot execution ordering, `reusable_rule_not_saved` still permits once-only execution).
- File tools: `files.list`/`files.glob`/`files.search`/`files.read` (streaming enumeration, ordinal relative paths, hidden/binary/protected skip with counts, result/byte/time caps, offset beyond EOF, UTF-8 boundary, bounded `truncated` metadata, junction/symlink containment, no unbounded allocation) plus hash-guarded `files.write`/`files.edit` (required `expected_sha256` `missing`/64-hex, `workspace_changed` on mismatch, same-volume temp + flush/atomic replacement, 4 MiB cap, UTF-8, exact occurrences, before/after hashes, bounded diff summary, run-attributed change set) and recoverable `files.delete`/`files.restore` (encrypted recovery copy capped 1 MiB, `recovery_too_large`, opaque `recovery_id`, one-time `recovery_consumed`/`recovery_target_exists`, byte-identity, never in diagnostics). `files.apply_patch` remains an internal/non-advertised adapter and is not model-usable.
- Worker v2 model surface: `files.list`, `files.read`, `files.glob`, `files.search`, `files.write`, `files.edit`, `files.delete`, `files.restore`, `terminal.run`, and `terminal.shell` are advertised in both Standard and Full Access. Configured deployments may additionally advertise `web.fetch` and `web.search` when the corresponding WebOptions and session scopes are present; these read-only external tools return bounded, explicitly untrusted results. Standard applies analyzer/rule/approval decisions; Full Access normally allows well-formed calls within the standard timeout window. Both run as the current Windows user and can execute host code or open sockets. The managed Git commit workflow (Phase 3 Task 6) is not shipped; Git commands are ordinary terminal requests rather than a separate managed capability.
- Deterministic device path: `system.volume` is Broker-backed but is not in the Worker v2 model list. Explicit, unambiguous volume wording is parsed before model selection and succeeds only after same-endpoint readback verification. `notes.create`, read-only `system.*`, and `app.list` remain internal Broker bindings.
- CLI tracer: thin headless `RuAgent.Cli` delegates start/attach to `RuAgent.Client` and all run behavior to HTTP/SSE; renders only safe envelope fields (NDJSON `v:1` with `--json`, no private payload auto-fetch, no prompt/args/output/secret fields) and now exposes `agent approve --scope once|run|project`/`deny`/`policy list`/`import --global FILE`/`remove` plus `run --preset full` interactive warning (`--json` cannot bypass).

The current Agent flow is:

```text
RuApp -> ASP.NET Core public contract -> typed worker protocol v2 -> managed C# Worker
ASP.NET Core public device calls -> typed worker protocol v1 -> managed C# Worker
managed C# Worker -> model adapter and typed runtime/device adapters -> Voice Engine / STT / VTS / observation
```

The typed Worker protocol v2 is the active Agent execution path. The framed
Voice Engine remains an accepted future target, not a claim about the current
Flask-backed implementation. The shipped [OpenCode-style system-prompt
composer](superpowers/specs/2026-08-29-ru-opencode-system-prompt-composer-design.md)
does not introduce a second conversation transport: text, voice, coding, and
tool-selection steps all use the same Agent run and Worker v2 path.

## Agent prompt contract

For every accepted model step, the Host rebuilds a bounded context and the
private Worker composes exactly one `system` message. The seven ordered layers
are Ru persona/safety, Agent execution contract, environment, root-selected Ru
instructions, profile/preset, host-derived capabilities, and runtime context.
The Host persists only the receipt digest metadata in an `InternalOnly`
`PromptReceipt` payload referenced by `model.prompt_composed`; the raw prompt,
instruction text, memory, tool results, arguments, paths, and provider data do
not enter history, diagnostics, or public SSE. Public SSE advances over the
internal event without emitting it or its payload reference.

The instruction loader considers only `Ru.md`, `.ru/AGENTS.md`, and
`.ru/CONTEXT.md` below the selected run root. It performs final-path containment,
strict UTF-8 decoding, NFC and newline normalization, a 64 KiB per-file cap,
and a 256 KiB combined instruction cap. The typed context has bounded memory
(100 KiB), references (32 KiB each), and a 512 KiB serialized-context cap.
Invalid context or receipt metadata fails closed with the bounded
`prompt_contract_invalid` path; there is no static-prompt fallback or reduced-
context retry. Web search/fetch are not advertised by default; a deployment
must configure WebOptions and grant the matching session scopes before either
tool appears. Returned web text is bounded, sanitized, and marked as
untrusted reference data.

Prompt composition is not an authority boundary. Broker policy, approvals,
workspace trust, protected paths, external-directory grants, and cancellation
remain authoritative. Declarative Skills are available only through explicit
session grants; subagent orchestration, LSP, extension/plugin/MCP discovery,
arbitrary system overrides, Chat compatibility, UI redesign, and the future
OS-enforced filesystem/network sandbox are outside this implementation.

## Harness ownership

The vNext compatibility Harness is a test-only owner. Its CLI creates a
temporary data/log root, three dynamically leased loopback ports, memory-only
bearers, and one Worker, Broker, and ASP.NET Core edge child. While the port
leases are still held, it prebuilds Broker and edge sequentially; both are then
started with `--no-build` so their shared `RuAgent.Protocol` output cannot race
during a cold build. It classifies all 56 HTTP and 1 WebSocket contract entries
(41 legacy plus 15 additive `/agent/v1/*`), uses deterministic fixtures for safe
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
| ASP.NET Core Public Host (`RuBackend.exe --managed-runtime`) | Authentication, limits, all 56 HTTP and 1 WebSocket route (41 + 15 `/agent/v1/*`), settings transactions, credentials, sessions, history, activity, structured memory, Agent run ordering, public error mapping, metadata filtering, durable Harness (Core state machines incl. `tool.execution_requested`, SQLite WAL + AES-GCM payloads, conversation outbox, workspace trust with valid `WorkspaceIdentity`, session permissions with monotonic version, encrypted policy snapshots `PolicySnapshot`/`PolicyRule`/`PolicyMatcher` with `Sha256` and `PolicySnapshotCanonicalizer` ordering, `FileRecoveryStore`/`RunChangeSetStore`, approval ordering `consume → persist reusable rule → execute under N → receipt → N+1`), Agent SSE (`id:<sequence>` cursors, `cursor_mismatch`, transient `t` IDs), and Worker/Broker child ownership in one kill-on-close Job Object with 10-min idle lease (active run/approval/long-lived lease suppresses exit). |
| Managed C# Worker | Default private Worker v2 Agent generation and Worker v1 runtime/device orchestration on separate loopback ports. It receives the single-use DPAPI provider bootstrap for v2 plus a distinct device token for v1; the bootstrap path is cleared before the v1 graph is composed. |
| Python Compatibility Workers | Explicit rollback-only Python Agent/Device topology. These workers are not started in `Dotnet` mode and receive no managed provider bootstrap. |
| RuAgentBroker | Private execution boundary for model-visible tools, capability policy, approvals, bounded adapters, and verified action receipts; private `run.open` lease binds the immutable `PolicySnapshot` (preset, workspace identity, ordered rules, SHA-256) and canonical workspace root, validates hash before capabilities, and advertises model-usable `files.*` (`list`/`glob`/`search`/`read`/`write`/`edit`/`delete`/`restore` one-time). Explicit absolute paths outside the root can receive a host-generated canonical-directory grant for the current run or project; that grant is limited to read/create/edit and never bypasses protected/destructive gates. The Broker also exposes headless terminal bindings, `system.volume`, and `notes.create`. `files.apply_patch` is not advertised. It has no public HTTP contract. |
| Voice Engine | OmniVoice model lifecycle and native WAV synthesis inside its isolated environment; currently reached through Flask and replaced by framed process I/O in a later plan. |

Unknown public paths stop in ASP.NET Core. Python has no public HTTP fallback,
forwarding catch-all, route ownership selector, or alternate host mode.

## Contracts and transport

The canonical Public Contract is
[`backend-dotnet/public-route-contract.json`](../backend-dotnet/public-route-contract.json):
56 HTTP method/path pairs and 1 WebSocket route (`/voice/stream`, plus 15
`/agent/v1/*`); REST, WebSocket and SSE clients
authenticate with the desktop secret (bearer for HTTP/SSE). ASP.NET Core maps
every public response and error; `GET /agent/v1/runs/{runId}/events` is SSE
with durable `id: <sequence>` and reconnect via `Last-Event-ID` or
`?after_sequence=` with `cursor_mismatch` rejection.

Worker contracts are versioned typed WebSockets:

- Protocol v1 (`/worker/v1`) is the private managed C# Worker device adapter by default: `runtime.request`/`runtime.response`, voice, VTS, health, and observation operations remain; it has no generation or memory-extract messages. The Python implementation is compatibility-only.
- Protocol v2 (`/worker/v2`) is the private C# Agent Worker contract: `agent.step.start` → `agent.step.delta`* → `agent.step.done`/`error`, with `agent.step.cancel`, bounded schemas, `AssistantStep(text, tool_calls, finish_reason)` (one `generate_reply` per step), and stable `worker.*` error codes; it never accepts `chat.tool.result`.

Protocol v2 removes HTTP-shaped fields in favor of typed domain commands,
capabilities, bounded payloads, ordering, cancellation, and stable worker
errors. `RuAgent.Cli` never speaks Worker protocols directly; it is a thin
HTTP/SSE client.

Public `/voice/stream` audio is carried as binary WAV frames. In the current
implementation the managed C# Worker reaches OmniVoice over loopback HTTP on
port 5052. The accepted Voice Engine target uses bounded stdin/stdout control and
binary frames, keeps native-speed audio, and exposes no network listener.

Text turns are authenticated Agent runs consumed through the durable Agent SSE
stream. The only public WebSocket is `/voice/stream`; it carries binary WAV
frames and does not open a second text-generation transport. In the managed
default, observation control is an explicit disabled/unavailable surface until
the C# screen-capture/reaction loop is delivered; Python observation remains an
explicit compatibility rollback only.

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
