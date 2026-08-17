# Ru Architecture v2 Refactor Design

**Date:** 2026-08-13
**Status:** Approved design; written-spec review pending
**Scope:** Full repository refactor, legacy removal, and local workspace cleanup
**Execution:** Plans 1-3 complete and integrated into `main` (2026-08-13..17).
Plans 4-6 (typed worker protocol v2, OmniVoice framed process, WinUI voice
deepening and final integration) remain pending. The "Current evidence" section
below is the original 2026-08-13 audit snapshot; later measurements live in
`docs/validation/`.

## Goal

Replace the migration-era repository with an intentional architecture in which
ASP.NET Core remains the only public host, Python contains only runtime code
that materially requires Python, the private worker uses typed domain commands
instead of pseudo-HTTP, and the repository root contains only current source,
operations, assets, and documentation.

The target is an aggressive end-state redesign. Execution remains incremental:
every commit must leave its affected interface tested and the repository in a
working state.

## Approved decisions

- Clean both tracked source/documentation and ignored local artifacts.
- Delete all Llama 3.1/v6.x source, tests, scripts, configs, datasets,
  checkpoints, evaluations, and documentation after an exact preflight.
- Rename the Python distribution and import package from the legacy MiniGPT /
  Llama naming to `ru-runtime` / `ru_runtime`.
- Keep ASP.NET Core as the sole public HTTP and WebSocket host.
- Replace worker protocol v1 and its pseudo-HTTP commands with a typed protocol
  v2.
- Replace the OmniVoice Flask loopback server with a private framed child-
  process protocol; port `5052` disappears.
- Deepen the WinUI voice module so tests exercise its interface instead of
  reflecting into private implementation state.
- Preserve active LFM2.5, OmniVoice, user memory, sessions, settings, and all
  unrelated dirty-worktree changes.
- Work without sub-agents.
- Do not push unless the user asks.

## Current evidence

The audit found architectural and physical clutter rather than one isolated
problem:

- There is no `CONTEXT.md` and no ADR directory. Current decisions are spread
  across the root README and dated migration plans.
- `src/mini_gpt` contains 73 flat Python modules mixing the active runtime with
  training, corpus, evaluation, voice, VTS, and migration code.
- `_server_shared.py` is 1,079 lines and exposes broad mutable module state to
  runtime modules through private names.
- `worker_runtime_handlers.py` is 669 lines and carries chat, memory, generic
  runtime HTTP emulation, voice, and VTS behavior.
- `runtime_http.py` duplicates 27 public method/path pairs inside the private
  worker and returns HTTP-shaped responses even though no Python HTTP host
  remains.
- `LoopbackWorkerClient.cs` is 702 lines and combines transport, framing,
  codecs, cancellation, domain parsing, and error translation.
- `VoicePlaybackService.cs` is 981 lines. Some tests use reflection to inspect
  its private fields and methods, demonstrating that the current interface is
  not the effective test surface.
- The root README is 537 lines and mixes current operations with historical
  Llama datasets, rejected experiments, and migration history.
- CI still installs `.[dev,server,runtime,gguf]` even though the `server` extra
  was removed with FastAPI.
- Several tracked `scripts/test_*.py` files bypass authentication, import the
  deleted server, use undeclared dependencies, or directly manipulate VTS.
- Ignored clutter measured during the audit includes `.venv311` at about
  1.29 GB, `gui-winui3.bak` at about 219 MB, three corpus verification
  directories at about 16.8 MB total, and about 830 MB of rebuildable .NET and
  Python outputs.
- `.tmp/vcpkg` is about 1.44 GB but is still referenced by the native overlay
  build. It is not a blind-delete candidate.
- `main` is ahead of `origin/main` and contains unrelated dirty and untracked
  work. The refactor must not stage, overwrite, move, or delete that work.

The verified pre-refactor functional baseline is 401 Python tests, 219 ASP.NET
Core tests, 94 WinUI tests, a 38-HTTP/2-WebSocket public contract, a successful
self-contained Release build, and a real non-empty OmniVoice audio frame. Test
counts will legitimately fall when historical Llama tests are removed; the
behavioral interfaces, not the old counts, are the acceptance surface.

## Domain language

- **Public host:** the ASP.NET Core process accepting RuApp HTTP and WebSocket
  traffic.
- **Runtime worker:** the authenticated loopback Python process for model and
  hardware implementations that remain in Python.
- **Generation kernel:** model selection, lifecycle, cancellation, generation,
  and GPU ownership behind one Python module interface.
- **Voice engine:** the OmniVoice process in `omnivoice_env`; it receives framed
  commands and emits framed status or WAV data without opening a network port.
- **Public contract:** the frozen 38 HTTP method/path pairs plus `/chat` and
  `/voice/stream`.
- **Worker contract:** protocol v2 envelopes, typed operations, ordering,
  cancellation, errors, limits, and capabilities.
- **Protected user data:** settings, sessions, history, activity, structured
  memory, credentials, and reviewed conversation examples still used by the
  active product.
- **Legacy Llama assets:** any source, config, script, dataset, checkpoint,
  evaluation, or documentation whose only supported purpose is Llama 3.1 or a
  v6.x adapter.

## Target repository

```text
D:\LLM
|-- src\ru_runtime\
|   |-- worker\
|   |-- generation\
|   |-- models\
|   |-- voice\
|   |-- speech\
|   |-- vts\
|   |-- observation\
|   `-- memory\
|-- contracts\
|   `-- worker-protocol-v2.schema.json
|-- backend-dotnet\
|   `-- RuBackend\
|       |-- Features\
|       `-- Infrastructure\WorkerProtocol\
|-- gui-winui3\
|   |-- Features\
|   |-- Infrastructure\Backend\
|   `-- Shell\
|-- tools\
|   |-- training\lfm25\
|   |-- evaluation\lfm25\
|   |-- diagnostics\
|   `-- desktop-overlay\
|-- assets\voice\omnivoice\
|-- tests\
|-- docs\
|   |-- architecture.md
|   |-- adr\
|   |-- operations\
|   `-- validation\
|-- CONTEXT.md
|-- README.md
`-- ROADMAP.md
```

The package must not gain generic `shared`, `common`, `helpers`, or
`compatibility` dumping grounds. Pure helpers live with the domain whose
invariants they support. A seam exists only for a real process/network edge or
when at least production and in-memory test adapters justify it.

## Module design

### ASP.NET Core public host

ASP.NET Core keeps direct ownership of authentication, request limits,
settings, credentials, sessions, history, activity, structured-memory
persistence, chat orchestration, metadata filtering, public DTO validation,
and public error shapes.

Feature modules are organized by domain rather than by technical pass-through
type. Each public endpoint calls a domain interface. Runtime-dependent domains
use typed worker adapters internally; callers never construct paths or parse
worker envelopes.

The public contract remains unchanged. `public-route-contract.json` continues
to be checked against live ASP.NET endpoint metadata.

### Python generation kernel

The generation kernel absorbs model lifecycle, selected backend state, GPU
ownership, cancellation, generation, and hosted/local adapter selection. It
replaces the broad globals in `_server_shared.py` and the shallow coordination
split across `backend_core.py`, `headless_runner.py`, and worker handlers.

Model adapters remain separate where behavior actually varies: llama.cpp,
OpenCode, and Kilo. Settings and credentials arrive through explicit kernel
construction or typed load commands; consumers do not reach into module
globals.

ASP.NET remains responsible for conversation persistence and memory policy.
Python memory code retains only fact extraction needed during generation.

### Worker transport

The WebSocket transport is a remote-but-owned process seam. The production
adapter uses authenticated loopback WebSocket; tests use an in-memory adapter
through the same domain interfaces.

Transport implementation owns connection setup, handshake, framing, request
correlation, sequence enforcement, binary pairing, cancellation, timeout, and
connection failure. Domain codecs are internal to that implementation. The
large C# worker client may be split internally, but callers see only domain
interfaces.

### WinUI voice

The external voice interface used by Chat, Voice, and the app shell remains
small: configure, start a reply, append text, complete, cancel, speak explicit
text, and dispose.

A deep reply-session module hides buffering, generation prefetch, fallback,
latency state, cancellation, terminal ordering, and subtitle notifications.
Media playback and subtitle rendering are adapters at real seams. Tests assert
observable events and adapter calls; reflection into private state is deleted.

The playback-first subtitle invariant remains unchanged: audio playback starts
before non-blocking subtitle notification, and subtitle failure cannot delay
voice.

## Worker protocol v2

### Selected interface

Protocol v2 retains the proven WebSocket transport and replaces generic
runtime requests with typed operations. This avoids the weak typing of a
generic command envelope without adding the gRPC/Protobuf build and packaging
stack.

Operations are:

```text
worker.health
model.load
model.generate
model.cancel
memory.extract
voice.synthesize
voice.stream
voice.warmup
voice.release
voice.cancel
speech.transcribe
vts.status
vts.connect
vts.catalog
vts.action
vts.motion
vts.overlay
observation.preflight
observation.react
observation.cancel
observation.status
```

No worker payload contains an HTTP method, path, query string, content type,
HTTP status code, desktop token, API key, or credential value.

### Envelope and ordering

The version remains explicit and advances to `v = 2`. Every envelope contains
a request ID of at most 128 characters, a typed operation/event name, a
sequence number for ordered responses, and an operation-specific payload.

Streaming invariants:

- sequence numbers start at zero and are contiguous per request;
- cancellation is correlated by request ID;
- every accepted request emits exactly one terminal event;
- binary data is preceded by one control frame declaring its byte length;
- empty or unannounced binary frames are protocol errors;
- a disconnect cancels all requests owned by that connection;
- missing required capabilities fails readiness instead of falling back.

The current 8 MiB WebSocket frame ceiling remains. Control payloads are bounded
more tightly by the schema. Voice text remains bounded by the existing public
limit, and WAV remains binary.

### Error model

Python returns a domain error code, a stable category, and `retryable` state.
Categories are `validation`, `unavailable`, `cancelled`, and `internal`.
ASP.NET alone maps these errors to public HTTP status codes and user-facing
messages.

Logs may contain operation, domain error, elapsed time, sequence count, and a
hash of the request ID. They never contain prompts, replies, transcripts,
audio, tokens, credentials, or private voice/VTS metadata.

The canonical schema is versioned under `contracts/` and contract-tested by
both languages. Handwritten typed models are acceptable only while those tests
prove conformance and drift detection.

## OmniVoice process protocol

The voice engine stays in `omnivoice_env` because its Python and model
dependencies remain isolated from the main runtime environment. It no longer
runs Flask or listens on port `5052`.

The runtime worker launches it as an owned child process and communicates over
length-prefixed stdin/stdout frames:

- stdout is reserved exclusively for protocol frames;
- diagnostics go to stderr;
- commands are typed `health`, `warmup`, `synthesize`, `release`, and
  `shutdown`;
- a synthesis result contains a control frame followed by one bounded WAV
  frame;
- malformed framing, timeout, early exit, or empty audio fails the request and
  tears down the owned process;
- shutdown is graceful first, then bounded terminate/kill fallback;
- the existing native-speed/original-voice contract remains unchanged.

The child inherits the desktop Job Object so RuApp shutdown still cleans the
entire process tree.

## Persistent data and credential migration

The new canonical business-data location is `%LOCALAPPDATA%\Ru`. The current
`~\.minigpt` settings, history, sessions, and activity files are migrated
transactionally before source compatibility is removed:

1. Copy recognized files into a uniquely named staging directory.
2. Validate JSON/JSONL structure, session count, file sizes, and SHA-256 hashes.
3. Atomically rename the staging directory to the canonical location.
4. Start ASP.NET and the worker against the canonical location and run data
   contract tests.
5. Delete the legacy directory only after the live application reads the
   migrated data successfully.

`RU_DATA_ROOT` remains the test/isolation override. Structured memory under
`data\conversations` is not moved by this refactor.

Credential entries move from the legacy service name to `Ru`. The migration
reads each expected credential, writes it under the new name, reads it back,
compares without logging the value, and only then deletes the old entry. The
temporary migrator and legacy identifier are removed after the current
workspace has passed the credential gate. A failed comparison leaves the old
entry intact and fails the migration.

## Legacy deletion and local cleanup

Deletion uses a generated manifest containing absolute path, classification,
size, modified time, active-reference result, and recoverability. The manifest
must not contain file contents, credentials, user text, or audio.

### Delete

- Llama 3.1/v6.x source, tests, configs, scripts, datasets, checkpoints,
  evaluations, and dated documentation.
- Superseded FastAPI/ASP.NET migration plans after their still-current
  decisions have been recorded in ADRs.
- Broken one-off scripts that import the deleted server, bypass authentication,
  use undeclared `requests`, or directly inject VTS state.
- `.venv311`, `gui-winui3.bak`, `tmp_verify_fresh*`, old logs, Python caches,
  egg-info, .NET `bin/obj`, `pytest_v.log`, `rustup-init.exe`, generated
  `resources.pri.xml`, and unused duplicate voice assets.
- Llama-only entries inside `eval_results`, models, checkpoints, and data.

### Preserve

- `.venv`, `omnivoice_env`, active LFM2.5 model assets, active voice references,
  current LFM evaluation/training inputs, and the native overlay source.
- `.worktrees` and all unrelated dirty/untracked files.
- user settings, sessions, history, activity, memory, credentials, and current
  reviewed examples.
- current ASP.NET route and decommission validation until replacement
  validation documents are complete.

`.tmp\vcpkg` is moved to an intentional cache location or made reproducible by
the overlay build before its old copy is deleted. `gui-winui3.bak` has five
source-only paths not present in the current directory; those paths are checked
against Git history and the untracked full-XAML copy is explicitly classified
before deletion.

Tracked deletions remain recoverable from Git. Ignored model/checkpoint deletion
is permanent; the approved manifest/reference gate is therefore mandatory
immediately before deletion.

## Documentation design

- `README.md` contains only setup, run, current architecture, active model and
  voice requirements, and links to deeper documents.
- `CONTEXT.md` defines the domain language above and the ownership of each
  module.
- `docs/architecture.md` describes current process/data flow, not migration
  history.
- ADRs record ASP.NET-only public ownership, typed worker protocol v2,
  OmniVoice process isolation, canonical data location, and deletion of the
  legacy Llama line.
- `ROADMAP.md` contains only pending LFM2.5 and product work. Completed Llama
  history is removed.
- Operations documents contain setup, build, diagnostics, backup/restore, and
  hardware smoke instructions.
- Validation documents contain current measured evidence and explicitly
  separate automated checks from human audio, microphone, and VTS acceptance.

## Migration sequence

Implementation uses an isolated `codex/ru-architecture-v2` worktree created
from the current `HEAD`. The existing dirty `main` worktree remains untouched.
The final verified branch is integrated back into `main` only after overlap
with user-owned dirty changes has been reviewed.

1. Add the architecture spine, ADRs, import rules, and corrected CI.
2. Produce the deletion manifest and remove approved Llama-only tracked/local
   assets while protecting active data.
3. Introduce `ru_runtime` domain packages and move code in vertical slices.
   A temporary import shim may keep intermediate commits green.
4. Migrate business data and credentials, prove them readable, then remove the
   temporary legacy migrators and import shim.
5. Add worker protocol v2 beside v1 and port one domain at a time.
6. Switch all callers to v2, run parity/contract gates, then delete v1,
   pseudo-HTTP dispatch, and compatibility code.
7. Replace the OmniVoice Flask process with framed stdio, prove real audio, and
   remove Flask and port-5052 behavior.
8. Deepen the WinUI reply-session module and replace reflection tests with
   interface tests.
9. Reorganize remaining ASP.NET/WinUI files by domain without changing the
   public contract.
10. Complete generated-artifact cleanup, current documentation, full gates,
    benchmark comparison, and integration review.

Each step is split further in the implementation plan so every commit has one
reviewable behavioral result and its own test cycle.

### Plan decomposition

This umbrella design is intentionally larger than one implementation plan. It
is executed through six independently reviewable plans in dependency order:

1. **Architecture spine and cleanup preflight** — add current domain/decision
   documents, fix CI, add architecture guards, and produce the exact deletion
   manifest without deleting protected data.
2. **Legacy removal and workspace hygiene** — remove approved Llama-only
   tracked/local assets, broken diagnostics, and rebuildable clutter; leave the
   current runtime behavior unchanged.
3. **Python package and persistence migration** — introduce `ru_runtime`, move
   domain modules, migrate data/credentials, and remove legacy import/storage
   compatibility after verification.
4. **Typed worker protocol v2** — add the schema and adapters, migrate every
   domain, then remove protocol v1 and pseudo-HTTP.
5. **OmniVoice framed process** — replace Flask/port 5052 while preserving
   native-speed audio, lifecycle, and privacy behavior.
6. **WinUI voice deepening and final integration** — replace reflection tests,
   complete ASP.NET/WinUI domain layout, update current documents, run all live
   and performance gates, and reconcile the result with `main`.

No later plan starts until the prior plan's full acceptance gate passes. A
failure can therefore be rolled back to the preceding working plan without
discarding the entire redesign.

## Verification

### Per-slice

- Add a failing behavior or architecture test.
- Verify that it fails for the intended missing behavior.
- Implement the smallest complete slice.
- Run focused tests, then the affected project suite.
- Run `git diff --check` and inspect staged scope before commit.

### Final automated gates

- all remaining Python tests;
- all ASP.NET Core tests;
- all WinUI tests;
- 38 HTTP plus 2 WebSocket public contract;
- C# and Python worker-protocol-v2 contract tests;
- architecture tests rejecting legacy package imports, pseudo-HTTP worker
  fields, FastAPI/Flask/Uvicorn dependencies, active Llama-only code, and
  reflection through the WinUI voice module;
- `pip check` and NuGet transitive vulnerability audit;
- self-contained Release build producing `RuBackend.exe` and `RuApp.exe`;
- deterministic chat/session/memory/activity/privacy gate;
- real model/worker/OmniVoice binary-audio/cancellation/cleanup gate;
- no listener on port `5052` and no surviving owned child after shutdown;
- current-document link and stale-reference scans;
- clean refactor worktree and preservation of unrelated main-worktree changes.

Heavy Python, .NET, native, and live runtime checks run sequentially on this
machine.

### Performance gate

Capture a fresh baseline immediately before the affected protocol slice and
repeat it on the same machine after the slice. Local route and handshake p50/
p95 use at least 20 samples with zero failures. Any p95 regression above 25%
is investigated rather than dismissed as noise. Model inference, cloud latency,
STT, and synthesis are reported separately from routing overhead.

### Hardware acceptance

Automated voice acceptance proves a non-empty WAV frame and lifecycle cleanup;
it does not prove perceived quality. Real microphone transcription, speaker
listening, and user-observed VTube Studio actions remain explicit user-run
acceptance gates. The refactor must report them as unverified until the user
performs them.

## Completion criteria

- The root contains only intentional current source, tools, assets, operations,
  and ignored active runtimes/models.
- ASP.NET Core is still the sole public host and the public contract is
  unchanged.
- Python contains only implementations that materially require Python.
- The private worker contains no HTTP-shaped command or response semantics.
- Flask, FastAPI, Uvicorn, protocol v1, port 5052, and migration compatibility
  code are absent from the final active tree.
- Legacy Llama 3.1/v6.x source and local assets are absent according to the
  approved manifest.
- Deep modules expose small interfaces; tests do not reach through those
  interfaces to private state.
- README, `CONTEXT.md`, ADRs, architecture, operations, validation, and roadmap
  agree on the current system.
- User data and unrelated dirty work are preserved and verified.
- All final automated gates pass; unperformed hardware gates are stated
  honestly.
- No remote push occurs without an explicit request.

## Rollback

Every behavioral slice has a Git checkpoint before deletion or cutover. Worker
v1 remains available until every v2 domain passes its contract and live gate.
The OmniVoice Flask path remains available until framed-process audio passes.
Temporary compatibility is removed only in the final commit of its slice.

Tracked source is recoverable from Git. The deletion manifest is the final
audit record for ignored assets, but it is not a backup. Because the user has
approved permanent removal of obsolete Llama assets, the implementation may
delete them after exact path, active-reference, and protected-path checks pass.
