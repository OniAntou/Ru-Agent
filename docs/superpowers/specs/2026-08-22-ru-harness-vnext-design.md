# Ru Harness vNext Design

**Status:** Implemented on main; deterministic GUI acceptance passed 2026-08-22
**Date:** 2026-08-22
**Scope:** Deterministic backend Harness and deterministic GUI voice Harness

## 1. Context and baseline

Ru currently has useful Harness coverage, but the coverage is concentrated in a
single compatibility script and does not yet exercise the complete ownership,
voice, or desktop lifecycle. The baseline was measured from `D:\LLM` on the
current `main` checkout:

- The public contract contains 41 HTTP routes and 2 WebSocket routes.
- The compatibility Harness probes 17 read-only HTTP routes and records 24
  mutating routes as skipped. The skips do not currently use isolated fixtures.
- The deterministic chat run exits successfully, but both child processes report
  exit code `1`; the Harness records those values without distinguishing an
  expected Harness shutdown from an unexpected child failure.
- The Harness uses fixed default ports, so an unrelated process can create a
  collision before the test starts.
- Authentication is checked with the configured bearer, but a wrong bearer is
  not required to fail.
- The deterministic worker supports synthetic chat and memory behavior, but
  voice is marked unsupported. The optional real voice check only exercises
  production TTS output and requires a real runtime.
- `MainWindow` owns backend startup at the composition root, while
  `ChatView` constructs its production voice capture service directly. The
  existing voice frame-source seam is therefore available to unit tests but not
  to a launched desktop Harness.

The existing route contract remains the source of truth. This design adds test
capability around that contract and does not add public product routes.

## 2. Goals

1. Make backend compatibility checks deterministic, repeatable, and safe for
   CI and local development.
2. Prove that the Harness is talking to the processes it started, using dynamic
   ports, owned credentials, readiness checks, and explicit process semantics.
3. Exercise every safe route and execute deterministic mutating routes against
   isolated temporary data with verified cleanup.
4. Add deterministic STT and TTS behavior so the voice request/response path is
   testable without a model, microphone, speaker, or VTube Studio.
5. Run the real WinUI shell through a typed composition seam and UI Automation,
   while replacing only the physical audio boundaries with deterministic test
   doubles.
6. Detect stop, cancellation, session replacement, stale results, private
   metadata leakage, child failures, port leaks, and temporary-data leaks.
7. Produce a versioned, privacy-safe artifact that identifies the failing
   component without storing credentials, audio, transcripts, or request bodies.
8. Keep the Harness split into small reusable components so backend, protocol,
   and GUI checks can evolve independently.

## 3. Non-goals and acceptance boundary

- No local model, training, dataset, evaluation corpus, or model benchmark is
  restored. The deterministic worker is a protocol fixture only.
- No new public backend route or WebSocket route is introduced.
- No subjective conversation-quality score is part of this phase. A later
  quality-evaluation phase may consume the deterministic lifecycle artifacts,
  but it is a separate design and acceptance decision.
- CI does not access a physical microphone, speaker, camera, or VTube Studio.
- Successful deterministic GUI checks do not claim that live microphone,
  speaker, OmniVoice, or VTube Studio behavior is accepted. Those remain a
  separate manual gate.
- The Harness does not silently alter user data or an existing running Ru
  installation. Its generated data is scoped to a unique temporary session.

## 4. Architecture

### 4.1 Harness core

The current compatibility script should become a thin command-line entry point
over a reusable Harness core. The implementation should keep process handling,
route fixtures, protocol probes, artifact writing, and CLI parsing in separate
modules. A suitable package boundary is:

- `processes`: owned child lifecycle, readiness, bounded stdout/stderr capture,
  process-tree termination, and exit classification;
- `ports`: loopback port leasing and post-run ownership checks;
- `fixtures`: route classifications, deterministic request data, isolated data
  roots, and rollback/cleanup behavior;
- `protocols`: HTTP, WebSocket, chat, memory, voice, and VTS contract probes;
- `artifacts`: schema-versioned safe result construction and redaction;
- `workers`: deterministic worker handlers, including synthetic STT/TTS;
- the existing script: CLI compatibility and orchestration only.

The core must expose one session object that owns all resources created for one
run. Cleanup is idempotent and runs from a `finally` path even when startup,
readiness, or a probe fails. A failed check must not prevent independent cleanup
checks from running.

### 4.2 Process and port ownership

By default, a Harness run leases free loopback ports for the worker and edge.
Explicit ports remain available for diagnosis, but the default path must not
depend on 8005 or 8010. A lease is held until the child startup handoff. If a
bind conflict occurs during handoff, the run fails with a `port_conflict` result
and does not retry against an unknown process.

The session starts the deterministic worker and ASP.NET edge with generated
session credentials and a unique temporary root. Readiness requires both
processes to be alive and the edge to answer the expected readiness contract.
The session then proves ownership on an authenticated route:

1. the generated bearer receives the expected successful response;
2. a random wrong bearer and a missing bearer receive the contract's rejected
   status (`401` or `403`); and
3. the process that bound the leased endpoint is the process tracked by the
   session, with no pre-existing listener accepted as a substitute.

Credentials are held in memory or in a restricted, short-lived harness session
descriptor. They are never written to the result artifact, normal diagnostics,
or child-output artifact.

Process semantics are explicit:

- A child that exits before a successful readiness checkpoint is a failure,
  regardless of its numeric exit code.
- A ready child that exits before the Harness requests shutdown is a failure.
- After a healthy Harness shutdown is requested, the termination code is
  recorded as `expected_stop` when the process was ready and no crash was
  observed. A nonzero code is still surfaced, but it does not mask the fact
  that the Harness requested termination.
- A child that refuses termination, leaves a listener open, or leaves a process
  in the process tree is a cleanup failure.

### 4.3 Route fixture registry

Every route in `backend-dotnet/public-route-contract.json` receives a registry
entry with its method, protocol, safety class, probe, and cleanup policy. The
registry must produce one final status for every route:

- `passed`: the route was exercised and matched its contract;
- `failed`: the route was exercised and did not match;
- `skipped`: the route cannot run in deterministic mode, with a required reason
  and category such as `hardware_required` or `external_service_required`.

Read-only routes are probed directly with deterministic headers and bounded
timeouts. Deterministic mutating routes run against a fresh fixture root with
seeded minimal state. The probe verifies the response and then verifies that
the fixture root is removed or restored according to the route policy. A route
that is destructive, hardware-bound, or externally side-effecting is not
executed merely to increase a count; it is explicitly classified and reported.

The artifact must distinguish total, passed, failed, and intentionally skipped
routes for HTTP and WebSocket coverage. A skipped route without a registry
reason is itself a Harness failure.

### 4.4 Deterministic voice worker

The deterministic worker implements the existing voice contracts used by Ru:

- `/voice/transcribe` accepts a generated PCM WAV fixture and returns a fixed
  test transcription without invoking STT or a model;
- `/voice/stream` returns a deterministic valid WAV stream with the response
  framing and terminal event expected by the production client; and
- the voice WebSocket control probe exercises valid and invalid control
  messages through the existing protocol.

The WAV fixture has a stable format, bounded duration, and a checksum recorded
only as a safe test statistic. The TTS assertion checks content type, RIFF/WAV
structure, sample format, nonzero payload, bounded duration, and a single
terminal completion. The STT assertion checks that the worker rejects malformed
audio and accepts the known valid fixture.

The deterministic worker may return a fixed assistant response and test
metadata internally, but the probe must assert that private VTS/voice metadata
does not enter visible chat text, history, memory, or the Harness artifact.
Audio bytes, transcription text, and request bodies are not persisted in the
artifact.

### 4.5 GUI composition boundary

The desktop Harness needs the production `RuApp` shell with deterministic
dependencies at its physical boundaries. The composition root should expose a
typed `RuAppComposition`/runtime-options object rather than scattering harness
environment checks through `MainWindow` and `ChatView`.

Normal mode keeps the current ownership model: `MainWindow` starts and stops
`BackendProcessManager`, and production audio services use the real devices.
Harness mode uses an explicit session descriptor containing only the external
edge endpoint, the session bearer, and the selected deterministic audio modes.
In this mode:

- `MainWindow` does not start another backend;
- the existing API/WebSocket clients use the external edge session;
- `ChatView` receives an injected `IVoiceFrameSource` factory;
- voice playback receives an injected recording sink or playback adapter; and
- the app's visible chat/history path remains the production path.

The launched app receives a descriptor path or equivalent local session handle,
not a bearer embedded in command-line arguments. The descriptor is restricted,
short-lived, deleted after it is consumed, and excluded from diagnostics. A
pure C# composition seam must also be available to unit tests so the harness
mode can be validated without launching a window.

The deterministic frame source emits one bounded turn and then a second turn
only when the test explicitly requests it. It must not open the microphone or
create an AudioGraph. The recording playback sink records play/start/end/error
events in memory and never opens the speaker. Existing production capture,
transcription, chat, and reply sequencing remains under test; only the physical
input/output adapters are replaced.

### 4.6 GUI automation driver

The GUI Harness should be a small typed Windows UI Automation driver rather than
a collection of ad hoc PowerShell selectors. It launches the published RuApp
with the session descriptor, locates controls by stable `AutomationId`, and
emits a bounded structured result for the orchestrator.

The driver must use explicit timeouts and state assertions. It must close the
app through the normal path first, then use process-tree cleanup if the app does
not exit. It runs only in an interactive Windows desktop job; the headless
backend Harness remains usable on ordinary CI workers.

## 5. Deterministic end-to-end flow

The full deterministic run has this sequence:

1. Create a unique temporary session root, generated credentials, route fixture
   registry, and dynamic loopback port leases.
2. Start the deterministic worker and ASP.NET edge, then verify readiness and
   bearer ownership.
3. Run route, WebSocket, chat, memory, privacy, and synthetic voice checks.
4. Create the restricted GUI session descriptor pointing at the owned edge.
5. Launch RuApp in Harness mode and wait for the main window and required
   automation controls.
6. Activate the voice button. The driver verifies the listening label and the
   `Đang nghe...` state without any microphone access.
7. Release the deterministic frame. RuApp performs the existing transcribe,
   chat, and reply flow. The driver verifies the user turn, assistant reply,
   recorded playback completion, and return to the listening state.
8. Request a second deterministic turn and verify that the next listening cycle
   is independent of the first.
9. Stop, cancel, and replace the session in separate cases. Verify no late
   prompt, reply, playback event, or private marker is applied after each
   invalidated operation.
10. Shut down the GUI, edge, and worker. Verify expected termination, closed
    ports, descriptor deletion, fixture cleanup, and temporary-root cleanup.
11. Write the safe artifact only after cleanup checks have been collected.

Backend-only runs stop after step 3. GUI runs may reuse the same owned backend
   session, but a failed backend prerequisite prevents GUI checks from being
   reported as false GUI failures.

## 6. Check matrix

| Check ID | Requirement | Pass condition |
| --- | --- | --- |
| `auth.correct_bearer` | Owned credential accepted | Expected protected response from the owned edge |
| `auth.wrong_bearer` | Credential isolation | Wrong and missing credentials are rejected |
| `routes.http_read_only` | Read-only contract coverage | Every eligible read-only route passes |
| `routes.http_fixture_mutation` | Safe mutation coverage | Fixture-backed mutation passes and rolls back/cleans up |
| `routes.websocket_contract` | WebSocket coverage | Each eligible WebSocket route passes its handshake/protocol probe |
| `chat.synthetic_order` | Conversation sequencing | Session/reset/chunk/done order and terminal state are correct |
| `chat.private_metadata` | Privacy boundary | No voice/VTS marker appears in visible or persisted chat state |
| `voice.synthetic_stt` | Deterministic transcription | Valid fixture succeeds and malformed fixture fails safely |
| `voice.synthetic_tts` | Deterministic reply audio | Valid bounded WAV and one terminal completion are observed |
| `voice.control_protocol` | Voice control protocol | Valid and invalid control messages match the contract |
| `gui.voice_start` | GUI activation | Button/state transition reaches listening without device access |
| `gui.voice_turn` | GUI one-to-one turn | Transcribe, send, reply, and playback completion are observed |
| `gui.voice_relisten` | Continued conversation | A requested second turn starts a fresh listening cycle |
| `gui.voice_stop` | Explicit stop | Stop returns to idle and prevents new capture work |
| `gui.voice_cancel` | Cancellation | Cancelled work produces no late UI or playback update |
| `gui.voice_stale_result` | Session invalidation | Old-session result is ignored after replacement |
| `gui.private_metadata` | GUI privacy | No private voice/VTS marker reaches visible chat/history |
| `lifecycle.child_exit` | Process ownership | No unexpected early exit; shutdown classification is explicit |
| `lifecycle.ports_closed` | Resource ownership | All leased ports are closed after cleanup |
| `lifecycle.temp_cleanup` | Data ownership | Fixture, descriptor, and temporary roots obey cleanup policy |
| `artifact.schema` | Result stability | Artifact validates against schema version 2 and contains no secrets |

The live hardware gate is documented separately and is not folded into the
deterministic pass count:

- real microphone capture works in the user's GUI;
- real speaker/OmniVoice playback is audible and correctly paced; and
- VTube Studio receives the intended expression/subtitle behavior without
  leaking control metadata into chat.

## 7. Artifact contract

The result is a JSON object with `schema_version: 2`. The minimum shape is:

```json
{
  "schema_version": 2,
  "harness_version": "vnext-1",
  "commit": "<full git sha or unknown>",
  "mode": "backend-deterministic|gui-deterministic|full-deterministic",
  "started_at": "<UTC ISO-8601>",
  "duration_ms": 0,
  "components": {
    "worker": { "status": "passed", "ready": true, "exit": "expected_stop" },
    "edge": { "status": "passed", "ready": true, "exit": "expected_stop" },
    "gui": { "status": "passed", "launched": true, "closed": true }
  },
  "checks": [
    { "id": "auth.correct_bearer", "status": "passed", "duration_ms": 0 }
  ],
  "coverage": {
    "http": { "total": 41, "passed": 0, "failed": 0, "skipped": 0 },
    "websocket": { "total": 2, "passed": 0, "failed": 0, "skipped": 0 }
  },
  "cleanup": {
    "children_terminated": true,
    "ports_closed": true,
    "session_descriptor_removed": true,
    "fixture_root_removed": true,
    "temporary_root_removed": true
  },
  "errors": [],
  "summary": { "status": "passed", "failed_checks": 0 }
}
```

The example values are structural only; the implementation must populate real
measurements. `checks` may contain a short safe reason for a skip or failure,
but must not contain credentials, audio bytes, transcripts, request/response
bodies, arbitrary environment variables, or unbounded child output. Paths are
omitted or reduced to stable component names. The artifact writer validates the
schema before the file is considered a successful Harness result.

## 8. Failure and cleanup semantics

- Readiness failure stops dependent probes, marks them `blocked_by_startup` in
  the internal result, and still runs all cleanup checks.
- Each probe has a bounded timeout. A timeout includes the component and check
  ID in the safe error entry and cannot leave an unbounded background task.
- An unexpected child exit, malformed protocol response, or privacy violation
  fails the run even if other checks pass.
- Cleanup is attempted in this order: stop GUI, revoke/delete the descriptor,
  request edge/worker shutdown, terminate remaining process trees, close port
  leases, remove fixture data, remove the session root, then validate the final
  state.
- If cleanup cannot complete, the run is failed and the artifact records only
  the failed cleanup check and a safe reason. A diagnostic retention option may
  preserve a user-selected temporary directory for investigation; it is never
  the default and is never inside the source tree.
- The Harness must not delete existing project files, model files, user data,
  or unrelated logs. Its cleanup target is the generated session root resolved
  before the run.

## 9. Implementation slices

Implementation should proceed in independently testable slices:

1. Extract the process/session, dynamic-port, auth, safe-artifact, and cleanup
   core while preserving the current CLI and deterministic chat behavior.
2. Add deterministic STT/TTS fixtures and voice protocol assertions.
3. Add the route fixture registry and execute eligible mutating routes against
   isolated temporary data.
4. Add `RuAppComposition` and the external-session descriptor, with unit tests
   proving normal mode still owns backend startup and Harness mode does not.
5. Inject deterministic voice input and playback boundaries into `ChatView` and
   add the typed UI Automation driver.
6. Add deterministic GUI lifecycle cases and merge their safe results into the
   versioned artifact.
7. Integrate backend checks into ordinary CI and GUI checks into an explicit
   interactive Windows job. Keep live hardware acceptance as a documented
   manual gate.

Each slice must add focused tests before implementation is considered complete.
No slice may add a second public route or reintroduce local model/training
artifacts.

## 10. Documentation impact

After implementation, update the following documents in the same slice that
changes their contract:

- `docs/operations/verification.md`: backend and GUI Harness commands, modes,
  artifact interpretation, and the separate live hardware gate;
- `README.md`: the supported verification entry points and the distinction
  between deterministic and live acceptance;
- `docs/architecture.md`: the composition boundary, external harness session,
  and ownership of backend/audio lifecycles;
- a new ADR under `docs/adr/` if the `RuAppComposition` and external-session
  boundary becomes a long-lived architectural decision.

Before implementation handoff, review existing verification and architecture
documents for statements that still describe fixed ports, unsupported
deterministic voice, or incomplete route coverage. Remove or correct outdated
statements rather than leaving contradictory instructions.

## 11. Definition of done for this design

This design is ready for implementation when the user confirms the spec. The
implementation is complete only when backend deterministic checks, GUI
deterministic checks where the interactive runner is available, artifact
validation, cleanup assertions, focused tests, and the documentation updates
all pass their applicable gates. Live microphone, speaker, OmniVoice, and VTS
acceptance remains explicitly reported as a separate user-run result.
