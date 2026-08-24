# Ru current architecture

Status: current ownership plus accepted architecture-v2 targets, 2026-08-24.

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
multiplexer (`/worker/v1` Chat, `/worker/v2` one-step Agent), starts the
isolated `RuAgentBroker` on a dynamic loopback port, binds ASP.NET Core on port
8000, waits for public readiness, and assigns Worker/Broker to one Job Object.
Idle Public Host exits after 10 minutes without an active Agent run, approval,
or long-lived Chat WebSocket/Agent SSE lease. All three owned children still
enter one Job Object, but ownership is Public Host, not WinUI. Startup succeeds
only after public readiness; shutdown or startup failure terminates the owned
Worker/Broker tree.

The current process flow (Phase 1 headless harness foundation) is:

```text
RuApp / RuAgent.Cli  --start-or-attach via RuAgent.Client-->  ASP.NET Core Public Host :8000
                                                                (RuBackend.exe --managed-runtime, owns one kill-on-close Job Object)
                                                                  |-- direct business, persistence, and durable Harness (Core + SQLite agent_harness.db + encrypted payloads)
                                                                  |-- private call to Runtime Worker :8005 (ru_runtime.worker.server: /worker/v1 + /worker/v2 one-step AssistantStep)
                                                                  `-- private call to RuAgentBroker execution boundary (run lease, files.list/read)
                                                                Worker/Broker owned children:
                                                                  |-- Runtime Worker :8005
                                                                  |     |-- hosted and external OpenAI-compatible provider adapters
                                                                  |     |-- Google STT, VTube Studio, and observation implementations
                                                                  |     `-- OmniVoice Flask service :5052 in omnivoice_env
                                                                  `-- RuAgentBroker
                                                                        `-- policy, approvals, and local action adapters
```

Additional Phase-1 surfaces (additive, feature-flagged `RuBackend:AgentHarness:Version=v1` default):

- Worker v2: one `agent.step.start` → exactly one `generate_reply` → `AssistantStep(text, tool_calls, finish_reason)`, with braced JSON never streaming as delta and provider errors mapped to bounded `worker.*` codes.
- Agent API/SSE: 13 `/agent/v1/*` routes plus durable `HarnessEvent` SSE (`id:<sequence>`, `Last-Event-ID`/`after_sequence` cursors, reconnect replay, transient non-durable deltas) with bearer auth, idempotency 409, `workspace_busy` 409, `preset_mismatch` 409, and `phase_not_available` for policy/approval/reconcile until their phase.
- CLI tracer: thin headless `RuAgent.Cli` delegates start/attach to `RuAgent.Client` and all run behavior to HTTP/SSE; renders only safe envelope fields (NDJSON `v:1` with `--json`, no private payload auto-fetch, no prompt/args/output/secret fields).

The accepted target flow, implemented by later architecture-v2 plans, is:

```text
RuApp -> ASP.NET Core public contract -> typed worker protocol v2 -> Runtime Worker
Runtime Worker -> model adapters / Voice Engine / STT / VTS / observation
```

The typed worker protocol v2 and framed Voice Engine are accepted decisions,
not claims about the currently running v1/Flask implementation.

## Harness ownership

The vNext compatibility Harness is a test-only owner. Its CLI creates a
temporary data/log root, three dynamically leased loopback ports, memory-only
bearers, and one Worker, Broker, and ASP.NET Core edge child. While the port
leases are still held, it prebuilds Broker and edge sequentially; both are then
started with `--no-build` so their shared `RuAgent.Protocol` output cannot race
during a cold build. It classifies all 54 HTTP and 2 WebSocket contract entries
(41 legacy plus 13 additive `/agent/v1/*`), uses deterministic fixtures for safe
mutations and voice plus additive Agent v2 read-only tracer fixtures
(idempotency, workspace trust, SSE cursor, payload binding), skips observation
routes that require hardware or external services, and removes the temporary
root during idempotent cleanup. An early child exit is recorded only as its
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
WebSocket clients, voice state machine, chat history path, and reply lifecycle
remain the same. Harness mode skips subtitle overlay and observation polling,
never takes over an existing RuApp instance, and does not report VTube Studio
as connected.

## Ownership

| Owner | Responsibilities |
| --- | --- |
| RuApp / RuAgent.Cli (via RuAgent.Client) | Desktop views (RuApp) or headless rendering (CLI), playback, microphone capture, public client calls via `AgentApiClient`/`SessionApiClient`/`AgentSseClient`, and user-visible state; start-or-attach Public Host through validated descriptor + `GET /status` bearer, but do not directly own Worker/Broker. |
| ASP.NET Core Public Host (`RuBackend.exe --managed-runtime`) | Authentication, limits, all 54 HTTP and 2 WebSocket routes (41 + 13 `/agent/v1/*`), settings transactions, credentials, sessions, history, activity, structured memory, chat ordering, public error mapping, metadata filtering, durable Harness (Core state machines, SQLite WAL + encrypted payloads, conversation outbox, workspace trust, session permissions), Agent SSE (`id:<sequence>` cursors), and Worker/Broker child ownership in one kill-on-close Job Object with 10-min idle lease (active run/approval/long-lived lease suppresses exit). |
| Runtime Worker | Provider adapters and device implementations that materially require Python: generation (v1 Chat multi-step + v2 one-step `AssistantStep`), STT execution, VTS, observation, and current OmniVoice coordination; reached via shared `ru_runtime.worker.server` (`/worker/v1` + `/worker/v2`). |
| RuAgentBroker | Private execution boundary for model-visible tools, capability policy, approvals, bounded adapters, and verified action receipts; private `run.open` lease advertises phase-1 `files.list`/`files.read` only. It has no public HTTP contract. |
| Voice Engine | OmniVoice model lifecycle and native WAV synthesis inside its isolated environment; currently reached through Flask and replaced by framed process I/O in a later plan. |

Unknown public paths stop in ASP.NET Core. Python has no public HTTP fallback,
forwarding catch-all, route ownership selector, or alternate host mode.

## Contracts and transport

The canonical Public Contract is
[`backend-dotnet/public-route-contract.json`](../backend-dotnet/public-route-contract.json):
54 HTTP method/path pairs and 2 WebSocket routes (`/chat` and
`/voice/stream` plus 13 `/agent/v1/*`); REST, WebSocket and SSE clients
authenticate with the desktop secret (bearer for HTTP/SSE). ASP.NET Core maps
every public response and error; `GET /agent/v1/runs/{runId}/events` is SSE
with durable `id: <sequence>` and reconnect via `Last-Event-ID` or
`?after_sequence=` with `cursor_mismatch` rejection.

Worker contracts are versioned typed WebSockets:

- Protocol v1 (`/worker/v1`) is the private Chat loop: `runtime.request`/`runtime.response` seam with HTTP-shaped fields remains for chat/memory/voice/STT/VTS/observation.
- Protocol v2 (`/worker/v2`, additive, feature-flagged) is one-step Agent: `agent.step.start` → `agent.step.delta`* → `agent.step.done`/`error`, with `agent.step.cancel`, bounded schemas, `AssistantStep(text, tool_calls, finish_reason)` (one `generate_reply` per step), and stable `worker.*` error codes; it never accepts `chat.tool.result`.

Protocol v2 removes HTTP-shaped fields in favor of typed domain commands,
capabilities, bounded payloads, ordering, cancellation, and stable worker
errors. `RuAgent.Cli` never speaks Worker protocols directly; it is a thin
HTTP/SSE client.

Public `/voice/stream` audio is carried as binary WAV frames. In the current
implementation the Runtime Worker reaches OmniVoice over loopback HTTP on port
5052. The accepted Voice Engine target uses bounded stdin/stdout control and
binary frames, keeps native-speed audio, and exposes no network listener.

The public `/chat` endpoint has one receive owner per WebSocket connection. It
places inbound frames on a bounded channel while a turn is running; the active
turn drains that channel for controlled `busy` responses, then the main loop
processes the next request only after the terminal `done` event. This preserves
sequential voice relistening and avoids concurrent `ReceiveAsync` calls racing
the next prompt.

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
chat text may enter generation and approved persistence, but transport-only
voice controls and private VTS actions, motions, overlays, timings, segment
identifiers, and model-control metadata must not enter chat, TTS text, history,
memory, reviewed examples, cleanup manifests, or logs.

Audio remains binary and manifests contain metadata only. Automated checks may
record statuses, timings, counts, event types, and cleanup state; they never
record credentials, bearer tokens, request bodies, session identifiers,
transcripts, audio bytes, or private VTS metadata.
