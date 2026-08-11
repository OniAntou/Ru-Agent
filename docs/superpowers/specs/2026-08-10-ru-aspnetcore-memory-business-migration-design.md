# Ru ASP.NET Core Memory and Business Logic Migration Design

## Status

Approved design. This document defines the follow-up migration after the
ASP.NET Core compatibility edge became the default public host on 2026-08-10.
It is a design record only; implementation is intentionally split into
independently verified plans.

Implementation status on 2026-08-11: the memory seam and the initial STT/VTS
runtime-worker ownership seams are implemented. STT/VTS still default to
Python, and their `aspnet` modes call the FastAPI compatibility worker; direct
hardware/provider migration and FastAPI decommission remain pending.

Execution authorization on 2026-08-11: the user delegated the remaining
architecture and cutover decisions and requested automatic FastAPI removal
after acceptance. The selected final architecture is ASP.NET Core as the only
public/control-plane host plus one private Python runtime worker over the
versioned loopback WebSocket protocol. Python remains only where the current
model, OmniVoice/STT, VTube Studio, or observation libraries are materially
safer and more capable than a rewrite in C#.

Two alternatives are explicitly rejected. A full C# rewrite of model and
hardware runtimes would add model-conversion, CUDA, audio, and VTS driver risk
without being required to remove FastAPI. Keeping FastAPI as an internal HTTP
worker would preserve the extra framework/process contract and would not meet
the requested decommission outcome.

FastAPI removal means all of the following, not merely changing the default:

- no `fastapi` or `uvicorn` project dependency;
- no `mini_gpt.server` process or FastAPI router modules in the active source;
- no ASP.NET HTTP catch-all/upstream dependency or `fastapi` host mode;
- every public route is a direct ASP.NET endpoint, with runtime-only calls sent
  to the private Python worker;
- FastAPI-specific tests are replaced by ASP.NET contract tests and Python
  worker-handler tests before deletion.

Deletion is allowed only after the route matrix has zero forwarded routes,
full .NET/Python/WinUI tests pass, the publish build succeeds, real non-empty
voice audio and live VTS behavior are accepted, worker/edge cleanup leaves no
listeners, and an isolated data restart preserves settings, sessions, memory,
and activity files. Git history is the rollback boundary after deletion; no
FastAPI runtime rollback remains in the shipped topology.

The completed transport cutover remains documented in
`docs/aspnetcore-cutover-validation.md`. The earlier
`2026-08-10-ru-aspnetcore-full-compatibility-and-cutover-design.md` document is
the historical baseline for that cutover, not the target design described here.

## Goal

Move Ru's memory and deterministic business logic ownership from the Python
FastAPI worker into ASP.NET Core while preserving the WinUI wire contract,
existing user data, privacy rules, chat event ordering, and a safe rollback.
Python remains a worker implementation for model inference, TTS/STT, VTube
Studio, and screen observation until separate worker slices replace its
FastAPI transport.

The final public topology is:

```text
WinUI REST/WebSocket :8000
        |
        v
ASP.NET Core public host
  |-- Memory module
  |-- Conversation module
  |-- Settings module
  |-- Activity module
  `-- WorkerClient seam
          |
          v
Python worker protocol on loopback
  |-- model inference
  |-- OmniVoice/STT
  |-- VTube Studio
  `-- screen observation
```

FastAPI remains an explicit compatibility adapter and rollback mode during the
migration. It is removed only after the public routes that depend on its
runtime have worker-protocol adapters and pass the final acceptance gates.

## Architectural vocabulary and constraints

The migration is organized around deep modules and explicit seams:

- A **module** owns one coherent concept and exposes a small interface.
- An **interface** defines types, invariants, ordering, configuration, and
  error modes that callers can rely on.
- A **seam** is the place where an implementation can be replaced without
  changing its callers.
- An **adapter** satisfies an interface for one concrete implementation, such
  as the existing FastAPI worker or the final Python worker protocol.
- **Depth** comes from keeping SQLite details, privacy rules, queue lifecycle,
  and protocol framing behind the memory, conversation, and worker interfaces.
- **Locality** comes from putting each ownership decision and its tests in one
  module rather than scattering it across routers and process globals.

The following constraints are load-bearing:

1. Each data category has exactly one writer at a time. Dual-read is allowed
   only for temporary comparison; dual-write is forbidden.
2. The existing SQLite schema, JSON session format, settings aliases, JSONL
   activity format, and WinUI response shapes remain compatible during the
   first migration.
3. Model inference and hardware-coupled runtime code are not ported as part of
   the memory slice. They are called through `WorkerClient`.
4. No bearer token, API key, chat text, audio bytes, or private VTS metadata is
   written to migration diagnostics or shadow-comparison logs.
5. A failed ownership switch is a restart-boundary rollback, never an
   automatic fallback that could leave two writers active.
6. Latency is measured rather than assumed. Removing the edge hop may reduce
   transport overhead, but it does not make Python model or TTS inference
   intrinsically faster.

## Current ownership baseline

The current FastAPI process owns both transport and most business behavior:

- `src/mini_gpt/memory.py` owns SQLite schema, retrieval, scoring, expiry,
  supersession, legacy import, and clearing.
- `src/mini_gpt/backend_core.py` owns the memory-store lifecycle and calls the
  model runner.
- `src/mini_gpt/_server_shared.py` owns settings/history/session paths, locks,
  the self-learning queue, and runtime lifecycle.
- `src/mini_gpt/routers/chat.py` owns the chat turn sequence, session writes,
  memory calls, marker stripping, VTS dispatch, and activity logging.
- `src/mini_gpt/routers/status.py`, `settings.py`, and `sessions.py` expose
  the memory, settings, activity, history, and session contracts.
- `backend-dotnet/RuBackend` owns authentication, request limits, HTTP
  forwarding, WebSocket frame relay, feature-selected memory handling, bounded
  STT validation, and explicit VTS runtime-worker routing.

The current storage contracts are intentionally different and must be made
explicit in one ASP.NET `DataPaths` module:

| Data | Current contract | First migration rule |
| --- | --- | --- |
| Structured memory | `data/conversations/memory.db` plus legacy artifacts | Keep the SQLite file/schema and IDs |
| Settings | `%USERPROFILE%\\.minigpt\\settings.json` | Keep keys, aliases, defaults, and atomic replacement |
| Legacy history | `%USERPROFILE%\\.minigpt\\history.json` | Read/migrate once with backup and hash |
| Sessions | `%USERPROFILE%\\.minigpt\\sessions\\*.json` | Keep JSON shape, ID rules, title rules, and limits |
| Activity | The existing `activity_log.py` path, including `RU_DATA_ROOT` override | Keep JSONL schema and 5,000-entry trim |

`RU_DATA_ROOT` remains available for isolated tests and live harnesses. The
ASP.NET path module must reproduce the current defaults instead of silently
consolidating files into a new directory.

## Worker protocol seam

The final Python runtime is a dedicated loopback worker, not a second public
HTTP host. The initial `WorkerClient` adapter may call the existing FastAPI
worker so the business modules can be migrated independently from transport.
The final adapter uses a versioned WebSocket protocol implemented with the
Python `websockets` library and ASP.NET WebSocket APIs:

```text
ASP.NET WorkerClient
  <-> ws://127.0.0.1:<worker-port>/worker/v1
      Python worker
```

The worker port is loopback-only, configurable, token-protected, and owned by
the same process manager as the ASP.NET host. It is not a new public WinUI
endpoint.

Every control message has a version, request ID, type, and payload:

```json
{"v":1,"id":"req-123","type":"chat.start","payload":{}}
{"v":1,"id":"req-123","type":"chat.chunk","seq":1,"payload":{"text":"..."}}
{"v":1,"id":"req-123","type":"chat.done","seq":2,"payload":{}}
{"v":1,"id":"req-123","type":"error","payload":{"code":"worker_unavailable"}}
```

The protocol must define these behaviors before the first production adapter:

- `hello` negotiates protocol version and authenticates the worker connection.
- `chat.start`, `chat.cancel`, and streamed `reset/chunk/done/error` events
  preserve public event ordering.
- Voice control messages carry request ID and sequence; a control event that
  announces audio is followed by its matching non-empty binary frame.
- Client disconnect cancels the worker request and releases the ASP.NET
  coordinator locks.
- Unknown versions, malformed envelopes, missing sequence numbers, or worker
  health failures fail closed with a deterministic error.
- TTS, STT, VTS catalog/action, and observation calls use the same versioned
  seam but are implemented and accepted in their own slices.

The business modules depend on `IWorkerClient`, not on a WebSocket class. This
keeps the FastAPI compatibility adapter and final worker adapter interchangeable
without changing memory or conversation code.

## Memory module design

ASP.NET introduces a deep `MemoryService` module with these interfaces:

```text
IMemoryRepository
  Get / ListActive / Retrieve / Upsert / Supersede / Clear

IMemoryPolicy
  Normalize / ValidateCandidate / RejectSensitiveOrUnsafe

IMemoryLearner
  ExtractFactsAsync(turn, IWorkerClient)

MemoryService
  RetrieveContext / RememberTurn / Clear / RunBackgroundLearning
```

### Storage compatibility

The first C# implementation uses `Microsoft.Data.Sqlite` and preserves the
existing `memories` and `legacy_memory_migrations` tables. It must retain:

- `MemoryKind` values `profile`, `preference`, `project`, `decision`, and
  `session_summary`;
- UUID IDs and `active`, `superseded`, and `deleted` statuses;
- `scope`, `evidence`, `confidence`, `importance`, `created_at`,
  `updated_at`, `accessed_at`, `expires_at`, and `pinned` semantics;
- WAL mode, bounded transactions, corruption quarantine, expired-record purge,
  and the current clear behavior;
- the current duplicate rule, supersession rule, expiry rule, scope filtering,
  2,000-record scan limit, scoring weights, minimum score, five-record limit,
  and context character limit;
- UTC serialization and the current Unicode/tokenization behavior, verified by
  golden fixtures rather than assumed from equivalent-looking C# code.

`GET /memory` is served directly by ASP.NET after the read-ownership gate and
returns exactly the WinUI DTO fields `id`, `text`, `category`, and `created_at`.
`DELETE /memory` remains under the Python writer until the C# queue and clear
invalidation behavior are proven.

### Privacy and self-learning

The C# policy must reproduce the rejection behavior currently shared by
`memory_extraction.py` and `memory_learning.py`. It rejects credentials,
passwords, tokens, URLs and private VTS/voice metadata. Rule-based extraction
is ported first. LLM-based extraction remains a worker call and keeps the
current maximum of five facts, confidence/importance bounds, evidence limit,
deduplication, and failure behavior.

The background learner is a bounded channel with capacity 50. A generation
counter invalidates queued turns when memory is cleared. Clear waits for the
active writer, increments the generation, drains stale work, and then deletes
memory artifacts. A worker failure is recorded as a bounded diagnostic and
never blocks the chat coordinator indefinitely.

### Ownership gates

1. Python remains the writer. C# opens the same database read-only for shadow
   comparison against synthetic fixtures and isolated live data.
2. ASP.NET owns `GET /memory` after all retrieval, ordering, expiry, Unicode,
   and DTO comparisons pass.
3. ASP.NET owns `DELETE /memory`, rule-based `upsert`, and self-learning only
   after the C# queue, clear invalidation, and worker extraction gates pass.
4. The Python memory writer is disabled before the ASP.NET writer is enabled;
   rollback selects the Python host after a restart.

## Deterministic business modules

### `DataPaths`

All settings, history, session, activity, and memory paths are resolved in one
module. The module accepts explicit test/runtime configuration while preserving
the existing defaults and `RU_DATA_ROOT` isolation behavior.

### `SettingsStore`

`SettingsStore` preserves the current defaults, aliases, bounded numeric values,
boolean fields, voice style normalization, and read-modify-write behavior.
Credential storage moves behind a C# adapter for the same Windows Credential
Manager service/account names currently used by `CredentialStore`. API keys
never enter settings JSON, diagnostic logs, or worker envelopes except as
short-lived in-memory runtime inputs required by the worker.

Backend/model selection is a transaction across runtime selection, worker load,
credential update, and settings persistence. Any failure restores the previous
runtime snapshot, credential snapshot, and settings file.

### `SessionStore` and `HistoryStore`

These modules preserve the current JSON contract, session ID validation, title
generation, maximum 200 stored messages, and maximum eight context turns.
Writes use atomic replacement and one process-level writer. The legacy history
file is migrated once into a new session only after a backup and source hash
are recorded; the old file is not deleted until the new session is durable.

`/sessions`, `/sessions/new`, `/sessions/{session_id}/activate`, and
`/sessions/{session_id}` move to ASP.NET before chat orchestration. `/history`
remains a compatibility read until consumers no longer depend on it.

### `ActivityLogStore`

The module preserves the JSONL event schema, chronological read order, bounded
limit of 500, append behavior, 5,000-entry trim, and clear behavior. It keeps
user-facing activity records compatible while ensuring migration diagnostics do
not copy chat text, audio, VTS metadata, or credentials.

## Conversation orchestration design

Chat moves from the route function into `ConversationCoordinator`. Its public
interface accepts a validated turn and produces public chat events; its
implementation coordinates these modules in a fixed order:

```text
validate request/session
  -> acquire TurnGate and MemoryGate
  -> load session context
  -> retrieve memory context
  -> load private VTS catalog when available
  -> call IWorkerClient.GenerateChatAsync
  -> sanitize streamed output and preserve reset/chunk ordering
  -> validate and remove VTS/voice metadata
  -> remember the cleaned turn
  -> append the assistant message exactly once
  -> trigger validated VTS actions through IWorkerClient
  -> emit done
  -> enqueue self-learning and activity recording
```

The coordinator keeps the current single-turn behavior: a second local
generation receives a busy event, a memory learner cannot corrupt a turn, and a
disconnect cancels the worker request. It does not know SQLite statements,
FastAPI routes, or VTS transport details.

`MetadataPolicy` is a mandatory seam. It ensures VTS actions, emotion markers,
voice pacing, and other private control data never enter visible chat, TTS
input, history, structured memory, reviewed examples, or activity diagnostics.

Voice streaming, VTS HTTP lifecycle, and screen observation remain separate
implementation slices. They use the same `IWorkerClient` seam but are not
bundled into the first chat migration because their cancellation, binary frame,
GPU, and external-process behavior has different acceptance gates.

## Migration phases and acceptance gates

| Phase | Ownership change | Required gate | Rollback |
| --- | --- | --- | --- |
| 0. Contract freeze | No ownership change | Baseline fixtures, source hashes, and data backup pass | Keep current default |
| 1. Worker seam | Add `IWorkerClient` and FastAPI adapter | Protocol envelopes and cancellation tests pass | Use FastAPI adapter |
| 2. Memory shadow-read | C# reads; Python writes | Retrieval, ordering, expiry, privacy, and DTO fixtures are identical | Disable shadow-read |
| 3. Memory read | ASP.NET owns `GET /memory` | WinUI Memory view and live read checks pass | Route back to Python on restart |
| 4. Deterministic stores | ASP.NET owns settings/session/history/activity one module at a time | Restart, concurrent write, corruption, and rollback tests pass | Restart with Python ownership |
| 5. Memory write | ASP.NET owns clear/upsert/self-learning | Queue generation, privacy, worker extraction, and single-writer checks pass | Restart with Python memory writer |
| 6. Chat | ASP.NET owns `ConversationCoordinator` and `/chat` | Event order, history, memory, metadata, cancellation, and error gates pass | Restart with FastAPI chat |
| 7. Runtime slices | ASP.NET routes voice/VTS/observation through worker adapters | Real audio, binary ordering, hardware lifecycle, and route matrix gates pass | Keep FastAPI host mode |
| 8. FastAPI removal | No public FastAPI dependency | All routes have direct ASP.NET implementation or worker adapter; process and data smoke pass | Restore the last tagged host mode |

Phase 7 is partially implemented: STT and VTS public route ownership seams and
their FastAPI worker adapter pass deterministic and isolated topology gates.
Real microphone transcription, a user-observed VTS action, the final versioned
worker transport, voice streaming ownership, and observation ownership remain
open acceptance work.

No phase enables two writers. A feature flag selects `python`, `shadow`, or
`aspnet` ownership for the current seam, and the process manager restarts at an
ownership switch. Automatic same-port fallback remains prohibited.

## Failure handling and rollback

- Malformed public requests keep the existing status codes and error shapes.
- Worker unavailable, protocol mismatch, or invalid worker frames produce a
  deterministic terminal error and a bounded diagnostic without request data.
- Cancellation propagates from WinUI disconnect to ASP.NET cancellation token,
  then to Python worker request cancellation.
- SQLite corruption is quarantined using the existing stamped-file behavior;
  the application must not silently discard the only copy.
- Settings/model transition failures restore runtime, credentials, and file
  state as one operation.
- Before every writer switch, the harness records file size, last-write time,
  and SHA-256 for memory, settings, history, sessions, and activity artifacts.
- Rollback is a controlled restart into the previous ownership mode. It never
  runs two writers and never performs an unreviewed reverse migration.

## Testing and verification

The implementation plans must add deterministic tests before each ownership
switch:

1. C# unit tests for each module's interface, invariants, error modes, and
   concurrency behavior.
2. Cross-language contract tests for worker handshake, envelope validation,
   request IDs, sequence numbers, cancellation, and binary audio pairing.
3. Golden compatibility fixtures generated from the current Python behavior,
   using synthetic text and temporary data roots only.
4. Shadow-read comparison that reports counts, hashes, and mismatch categories,
   never raw memory, tokens, chat, or audio.
5. Isolated live harnesses that start the worker/edge topology with temporary
   data and verify restart, worker failure, child cleanup, and no leaked ports.
6. Full sequential repository gates after each major phase:

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --no-restore
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py tests/test_voice_stream.py tests/test_vts_api.py -q
```

The final gate also reruns the route matrix checker, live chat/voice harness,
confirmed non-empty audio acceptance, build/publish, process-manager smoke,
`git diff --check`, and document review. A benchmark compares direct worker
calls with the final public path and reports overhead; it does not convert a
transport result into a claim that the model or TTS runtime is faster.

## Scope decomposition for implementation plans

This design is intentionally split into independent plans so each change is
testable and revertible:

1. Memory schema-compatible repository, policy, retrieval, shadow-read, and
   ownership switch.
2. DataPaths, settings, Credential Manager adapter, sessions, history, and
   activity modules.
3. Versioned Python worker protocol and `WorkerClient` adapters, including
   FastAPI compatibility and final loopback worker implementations.
4. ConversationCoordinator and public `/chat` ownership switch.
5. Voice, VTS, observation worker adapters and final FastAPI decommission.

Each plan must include exact files, failing tests before implementation, a
focused gate, a full sequential verification gate, and a documentation review.
The plans must not begin by deleting Python modules or changing the default
rollback mode.

## Documentation maintenance

At the end of every implementation slice, review and update:

- `README.md` for the actual default topology and rollback command;
- `backend-dotnet/README.md` for ownership, worker port, and protocol state;
- `docs/aspnetcore-route-matrix.md` and its JSON source for route ownership;
- `docs/aspnetcore-cutover-validation.md` for fresh evidence;
- this design and the active implementation plan for current status.

Historical design documents remain in place, but their status must clearly state
when they describe a completed baseline rather than current ownership.
