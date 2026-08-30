# Ru Harness vNext Backend Implementation Plan

> **Status:** Historical implementation record. The deterministic Harness
> shipped; current release gates live in `docs/operations/verification.md`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the current fixed-port, GET-only compatibility probe with a deterministic backend Harness that proves process/auth ownership, covers safe routes with isolated fixtures, exercises synthetic STT/TTS, and emits a validated privacy-safe artifact.

**Architecture:** Keep `scripts/aspnetcore_compatibility_harness.py` as the stable CLI entry point and move lifecycle, route fixtures, deterministic voice, and artifact construction into focused modules under `scripts/harness/`. One `HarnessSession` owns ports, processes, credentials, temporary data, and cleanup; every probe returns a typed safe check result and the final artifact uses schema version 2.

**Tech Stack:** Python 3.13, `httpx`, `websockets`, `pytest`, ASP.NET Core edge, private Python worker v1 protocol, JSON artifacts, Windows process/port lifecycle.

---

## 0. Scope and working-tree boundary

Only the backend Harness files listed in this plan, the backend CI contract, and
the verification documents are in scope. Work stays on `main`. Before every
commit, stage only the files named by that task. Preserve unrelated dirty files,
including the existing WinUI voice/meteor changes and cleanup-policy changes.

The current baseline to preserve while refactoring is:

- `check_aspnetcore_route_matrix.py` reports 41 HTTP routes and 2 WebSocket
  routes;
- `--deterministic-chat` reports ordered chat events and privacy flags; and
- the existing command continues to accept `--deterministic-chat`, `--output`,
  `--require-voice-audio`, and `--benchmark-samples` until the new modes replace
  them deliberately.

## 1. File map

Create these focused modules:

- `scripts/harness/__init__.py`: public Harness types and version constant;
- `scripts/harness/model.py`: enums and immutable result records;
- `scripts/harness/artifacts.py`: schema-v2 construction, redaction, and
  validation;
- `scripts/harness/ports.py`: loopback port leasing and closed-port checks;
- `scripts/harness/processes.py`: owned child start/stop, bounded logs, and exit
  classification;
- `scripts/harness/routes.py`: route registry, fixture data, safe request
  builders, and route coverage summaries;
- `scripts/harness/protocols.py`: HTTP/WebSocket probes, auth ownership, chat,
  memory, and voice assertions;
- `scripts/harness/voice_fixtures.py`: deterministic WAV generation and WAV
  inspection;
- `scripts/harness/deterministic_worker.py`: deterministic `WorkerHandlers`
  implementation and worker entry point;
- `scripts/harness/session.py`: `HarnessSession` resource ownership and
  orchestration.

Modify these existing files:

- `scripts/aspnetcore_compatibility_harness.py`: compatibility CLI wrapper only;
- `tests/test_aspnetcore_compatibility_tools.py`: retain public helper coverage
  and update expectations from silent mutating skips;
- `.github/workflows/ci.yml`: run the new deterministic backend mode and upload
  only the safe JSON artifact;
- `tests/test_ci_contract.py`: assert the new command and artifact upload;
- `docs/operations/verification.md`, `README.md`, and `docs/architecture.md`:
  document dynamic ports, route classifications, artifact v2, and the separate
  live hardware gate.

Create focused tests:

- `tests/test_harness_artifacts.py`;
- `tests/test_harness_ports.py`;
- `tests/test_harness_processes.py`;
- `tests/test_harness_routes.py`;
- `tests/test_harness_voice.py`;
- `tests/test_harness_session.py`.

Do not create a second public backend route, a model fixture, a training file,
or a persistent artifact directory inside the source tree.

## 2. Task 1: Define safe result models and artifact schema

**Files:**

- Create: `scripts/harness/model.py`
- Create: `scripts/harness/artifacts.py`
- Create: `scripts/harness/__init__.py`
- Test: `tests/test_harness_artifacts.py`

- [ ] **Step 1: Write the failing artifact/privacy tests**

Add tests that require the exact public data shape and prove sensitive values
are not retained:

```python
def test_build_artifact_uses_schema_v2_and_safe_check_details() -> None:
    artifact = build_artifact(
        commit="abc123",
        mode="backend-deterministic",
        started_at="2026-08-22T00:00:00Z",
        duration_ms=12.5,
        components={"edge": {"status": "passed"}},
        checks=[CheckResult("auth.correct_bearer", "passed", 1.2)],
        coverage={"http": {"total": 41, "passed": 41, "failed": 0, "skipped": 0}},
        cleanup={"ports_closed": True},
        errors=[],
    )

    assert artifact["schema_version"] == 2
    assert artifact["mode"] == "backend-deterministic"
    assert artifact["summary"] == {"status": "passed", "failed_checks": 0}


def test_artifact_rejects_secrets_and_body_like_details() -> None:
    result = CheckResult(
        "auth.correct_bearer",
        "failed",
        1.0,
        reason="Bearer private-token response body private prompt",
    )

    with pytest.raises(ValueError, match="unsafe artifact detail"):
        build_artifact(
            commit="abc123",
            mode="backend-deterministic",
            started_at="2026-08-22T00:00:00Z",
            duration_ms=1.0,
            components={},
            checks=[result],
            coverage={},
            cleanup={},
            errors=[],
            secrets=["private-token", "private prompt"],
        )
```

Import `pytest`, `CheckResult`, and `build_artifact` in the test. The first run
must fail because the new modules and types do not exist.

- [ ] **Step 2: Run the focused tests and record the expected failure**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_harness_artifacts.py
```

Expected: collection fails with `ModuleNotFoundError: No module named
'harness'`.

- [ ] **Step 3: Implement the result records and safe artifact builder**

Define these exact model values in `model.py`:

```python
CheckStatus = Literal["passed", "failed", "skipped", "blocked_by_startup"]

@dataclass(frozen=True, slots=True)
class CheckResult:
    check_id: str
    status: CheckStatus
    duration_ms: float
    reason: str | None = None
    details: Mapping[str, object] = field(default_factory=dict)

@dataclass(frozen=True, slots=True)
class ChildExit:
    role: str
    code: int | None
    classification: Literal["running", "unexpected_exit", "expected_stop", "cleanup_failure"]
```

`build_artifact` must emit `schema_version`, `harness_version`, `commit`,
`mode`, `started_at`, `duration_ms`, `components`, `checks`, `coverage`,
`cleanup`, `errors`, and `summary`. `summary.status` is `failed` when any check
has status `failed`, `blocked_by_startup`, or `cleanup_failure`; it is `passed`
only when no check fails. Reject strings containing a supplied secret, bearer
prefix, raw request-body keys, transcript fixture text, or binary payloads.
Keep only bounded scalar values, counts, event types, statuses, timings, and
safe reason codes.

Expose `HARNESS_VERSION = "vnext-1"` from `scripts/harness/__init__.py`.

- [ ] **Step 4: Run the focused tests and verify the result**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_harness_artifacts.py
```

Expected: all artifact tests pass and the serialized JSON contains no
`private-token`, `private prompt`, or request-body text.

- [ ] **Step 5: Commit the artifact boundary**

Run:

```powershell
git add -- scripts/harness/__init__.py scripts/harness/model.py scripts/harness/artifacts.py tests/test_harness_artifacts.py
git commit -m "test: define Harness artifact v2 boundary"
```

## 3. Task 2: Add dynamic ports and explicit child lifecycle

**Files:**

- Create: `scripts/harness/ports.py`
- Create: `scripts/harness/processes.py`
- Test: `tests/test_harness_ports.py`
- Test: `tests/test_harness_processes.py`

- [ ] **Step 1: Write the port and exit-classification tests**

Cover the exact invariants:

```python
def test_port_lease_uses_free_loopback_port_and_releases_it() -> None:
    lease = PortLease.acquire()
    assert lease.host == "127.0.0.1"
    assert lease.port > 0
    lease.close()
    assert wait_for_ports_closed([lease.port]) is True


def test_ready_child_exiting_before_stop_is_unexpected() -> None:
    child = FakeChild(exit_code=1)
    state = ChildLifecycle(role="edge", process=child)
    state.mark_ready()
    child.exit()
    assert state.poll().classification == "unexpected_exit"


def test_ready_child_exit_after_harness_stop_is_expected_stop() -> None:
    child = FakeChild(exit_code=1)
    state = ChildLifecycle(role="worker", process=child)
    state.mark_ready()
    state.request_stop()
    child.exit()
    assert state.poll().classification == "expected_stop"
```

The fake child exposes `poll`, `terminate`, `kill_tree`, and `wait` so no real
process is needed. The first run must fail because the modules do not exist.

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_harness_ports.py tests/test_harness_processes.py
```

Expected: collection fails with missing `PortLease` and `ChildLifecycle`.

- [ ] **Step 3: Implement port leasing without fixed defaults**

`PortLease.acquire()` must bind `127.0.0.1:0`, retain the socket until
`handoff()`, and expose the selected port. The default Harness path must use
two leases. Explicit `--worker-port` and `--edge-port` are diagnostic overrides;
an explicit bind conflict raises `PortConflictError` immediately. Do not retry
against another listener. `wait_for_ports_closed` must attempt a TCP connection
until the timeout and return `False` while any listener remains.

- [ ] **Step 4: Implement process lifecycle and bounded logging**

`ChildLifecycle` must track `role`, `ready`, `stop_requested`, `started_at`,
`ready_at`, `stop_requested_at`, exit code, and termination classification. A
child exiting before readiness or before `request_stop()` is
`unexpected_exit`. A ready child exiting after `request_stop()` is
`expected_stop`, even when its numeric code is 1, but the code remains in the
internal result. A failed terminate/kill/wait is `cleanup_failure`.

`start_owned_child(command, repo_root, environment, role, log_path)` must use a
new process group on Windows, redirect output to a temporary log, cap retained
diagnostic output at 64 KiB, and redact all session secrets before exposing a
safe error. The artifact stores only role, readiness, classification, and exit
code; it never stores child output.

- [ ] **Step 5: Run the lifecycle tests and verify they pass**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_harness_ports.py tests/test_harness_processes.py
```

Expected: all port and process tests pass, including a nonzero expected
shutdown code that does not become an unexpected crash.

- [ ] **Step 6: Commit lifecycle ownership**

Run:

```powershell
git add -- scripts/harness/ports.py scripts/harness/processes.py tests/test_harness_ports.py tests/test_harness_processes.py
git commit -m "test: harden Harness port and process ownership"
```

## 4. Task 3: Build the route registry and authenticated probes

**Files:**

- Create: `scripts/harness/routes.py`
- Create: `scripts/harness/protocols.py`
- Test: `tests/test_harness_routes.py`
- Modify: `tests/test_aspnetcore_compatibility_tools.py`

- [ ] **Step 1: Write route-classification and auth tests**

Require every route to have an explicit class and every skipped route to carry
an explanation:

```python
def test_registry_covers_the_current_contract_without_silent_skips() -> None:
    registry = build_route_registry(load_contract(Path("D:/LLM")))
    assert len(registry) == 43
    assert all(entry.safety in {
        RouteSafety.READ_ONLY,
        RouteSafety.FIXTURE_MUTATION,
        RouteSafety.EXTERNAL_SKIP,
    } for entry in registry)
    assert all(entry.skip_reason for entry in registry
               if entry.safety is RouteSafety.EXTERNAL_SKIP)


def test_auth_probe_requires_correct_success_and_wrong_rejection() -> None:
    client = FakeHttpClient({
        ("good", "/settings"): 200,
        ("wrong", "/settings"): 401,
        ("missing", "/settings"): 403,
    })
    result = probe_auth_ownership(client, "good", "wrong", "/settings")
    assert result.status == "passed"
    assert result.details == {"correct": 200, "wrong": 401, "missing": 403}
```

Use `Path(__file__).resolve().parents[1]` in the real test instead of relying
on the developer's current directory. Update the old mutating-route test so it
asserts `RouteSafety.FIXTURE_MUTATION`, not the former string skip reason.

- [ ] **Step 2: Run the route tests and verify the expected failure**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_harness_routes.py tests/test_aspnetcore_compatibility_tools.py
```

Expected: collection or assertion failure because the route registry and auth
probe do not exist yet.

- [ ] **Step 3: Implement the explicit route registry**

Create `RouteSafety` values `READ_ONLY`, `FIXTURE_MUTATION`, and
`EXTERNAL_SKIP`. Build one entry for each of the 41 HTTP and 2 WebSocket
contract entries. Use these concrete classifications:

| Route group | Classification | Fixture/probe |
| --- | --- | --- |
| `GET /status`, `/diagnostics/logs`, `/diagnostics/export`, `/diagnostics/status`, `/activity-log`, `/models`, `/history`, `/memory`, `/settings`, `/sessions`, `/voice/status`, `/vts/mouse-look`, `/vts/overlay`, `/vts/status`, `/vts/config` | `READ_ONLY` | Authenticated GET with bounded response/status validation; accept only documented optional-device 503 for the device status entries |
| `DELETE /activity-log`, `DELETE /memory`, `POST /settings`, `POST /sessions/new`, `POST /sessions/{sessionId}/activate`, `DELETE /sessions/{sessionId}` | `FIXTURE_MUTATION` | Seed temporary data, create/activate/delete a generated session where needed, then verify the fixture root state and remove it |
| `POST /voice/latency`, `/voice/tts`, `/voice/warmup`, `/voice/release`, `/voice/transcribe` | `FIXTURE_MUTATION` | Use fixed JSON payloads and deterministic WAV; no physical voice engine |
| `POST /vts/mouse-look`, `/vts/overlay`, `/vts/connect`, `/vts/config`, `/vts/activity`, `/vts/action`, `/vts/speaking` | `FIXTURE_MUTATION` | Send bounded synthetic payloads; deterministic worker returns protocol success without a VTS Studio connection |
| `/observation/preflight`, `/observation/vision-config`, `/observation/react`, `/observation/cancel`, `/observation/ack`, `/observation/start`, `/observation/stop`, `/observation/status` | `EXTERNAL_SKIP` | Record `hardware_or_external_observation_required`; do not capture a screen or invoke a cloud reaction in this Harness |
| `WS /chat`, `WS /voice/stream` | `READ_ONLY` | Dedicated chat and voice protocol probes with deterministic worker handlers |

The registry's `request_factory` for mutations must use these exact fixture
values: settings payload with `voice_speed=1.0`, `auto_read_replies=true`, and
`show_subtitles=false`; latency payload with `outcome="completed"` and finite
zero timings; TTS payload text `Harness reply`, voice label `Ru (OmniVoice)`,
speed `1.0`; session operations use the ID returned by `/sessions/new`;
VTS payloads use `enabled=false`, `sensitivity=1.0`, `speaking=false`, and
`codes=["vts_01"]`; STT uses the generated WAV and sample rate 16000. Never
store these request bodies in the artifact.

`probe_http_routes` must return one `CheckResult`-compatible record for every
contract entry. It may report `skipped` only for `EXTERNAL_SKIP`, with the
registry reason. A missing registry entry or an unclassified route is a
Harness failure. Response validation is fail-closed: 401, 404, 422, and 5xx
are failures unless the route's explicit optional-device policy allows 503.

- [ ] **Step 4: Implement auth ownership and WebSocket authentication**

`probe_auth_ownership` must call the protected `/settings` route three times:
the generated bearer must receive 2xx, the random wrong bearer must receive
401 or 403, and no bearer must receive 401 or 403. It must reject a successful
response for either invalid credential. Apply the same wrong-token rejection to
the `/chat` and `/voice/stream` WebSocket handshakes and report only close code
or status category, never the token or handshake body.

- [ ] **Step 5: Run route unit tests and verify they pass**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_harness_routes.py tests/test_aspnetcore_compatibility_tools.py
```

Expected: all route-classification, auth, fail-closed status, redaction, and
legacy helper tests pass.

- [ ] **Step 6: Commit route coverage**

Run:

```powershell
git add -- scripts/harness/routes.py scripts/harness/protocols.py tests/test_harness_routes.py tests/test_aspnetcore_compatibility_tools.py
git commit -m "feat: add explicit authenticated route fixtures"
```

## 5. Task 4: Add deterministic WAV, STT, and TTS fixtures

**Files:**

- Create: `scripts/harness/voice_fixtures.py`
- Create: `scripts/harness/deterministic_worker.py`
- Test: `tests/test_harness_voice.py`

- [ ] **Step 1: Write deterministic voice tests**

Require a stable PCM fixture and production-compatible voice events:

```python
def test_synthetic_wav_is_mono_pcm_with_bounded_nonzero_duration() -> None:
    payload = build_synthetic_wav()
    info = inspect_wav(payload)
    assert info == {
        "channels": 1,
        "sample_width": 2,
        "sample_rate": 16_000,
        "frames": 3_200,
        "valid": True,
    }


def test_synthetic_tts_stream_emits_audio_then_one_ended_event() -> None:
    events = list(collect_deterministic_stream(request_id="voice-1"))
    assert [event.type for event in events] == ["audio", "ended"]
    assert is_valid_wav(events[0].binary) is True
    assert events[-1].request_id == "voice-1"


def test_synthetic_worker_never_uses_model_or_device_runtime() -> None:
    assert deterministic_worker_dependencies() == {"model": False, "microphone": False, "speaker": False, "vts": False}
```

- [ ] **Step 2: Run the voice tests and verify they fail**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_harness_voice.py
```

Expected: missing fixture/worker symbols.

- [ ] **Step 3: Implement the stable WAV fixture and validator**

`build_synthetic_wav()` must use Python's `wave` module, one channel, 16-bit
samples, 16000 Hz, and exactly 3200 frames. Generate a bounded deterministic
speech-like sample sequence with nonzero frames followed by silence; it is a
protocol fixture, not a quality sample. `inspect_wav` returns only channel,
sample width, rate, frame count, and validity. It rejects malformed RIFF data,
zero frames, invalid sample dimensions, and payloads larger than 256 KiB.

- [ ] **Step 4: Implement deterministic worker handlers**

Move `_serve_deterministic_worker` out of the CLI into
`deterministic_worker.py`. Preserve the existing chat, memory, runtime, and VTS
test behavior. Add a `voice` handler that supports the worker operations used by
the public edge:

- `operation="tts"`: emit `VOICE_CONTROL` with the synthetic WAV as binary,
  then `VOICE_DONE` with state `done`;
- `operation="stream"`: emit one audio control event with the same WAV and one
  terminal `ended` event using the incoming request ID;
- `operation="warmup"` and `operation="release"`: emit one successful done
  event; and
- any other operation: emit `ERROR` with `unsupported_operation`.

Add runtime request handling for `/voice/transcribe` that validates the base64
fixture and returns `{"status":"ok","text":"Harness voice turn"}` only for
the known valid bytes. Return a safe error for malformed audio. Return the same
WAV for `/voice/tts`. The handler must not import model, STT, speaker, or VTS
runtime modules.

- [ ] **Step 5: Run focused voice and worker protocol tests**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_harness_voice.py tests/test_worker_protocol.py tests/test_worker_server.py
```

Expected: all tests pass, including one valid and one malformed STT request,
one valid TTS stream, one cancel signal, and one invalid control signal.

- [ ] **Step 6: Commit deterministic voice**

Run:

```powershell
git add -- scripts/harness/voice_fixtures.py scripts/harness/deterministic_worker.py tests/test_harness_voice.py
git commit -m "feat: add deterministic Harness voice fixtures"
```

## 6. Task 5: Compose the owned Harness session and preserve the CLI

**Files:**

- Create: `scripts/harness/session.py`
- Modify: `scripts/aspnetcore_compatibility_harness.py`
- Test: `tests/test_harness_session.py`
- Modify: `tests/test_aspnetcore_compatibility_tools.py`

- [ ] **Step 1: Write session cleanup tests**

Use fake process and HTTP seams to assert cleanup on readiness failure and probe
failure:

```python
def test_session_cleanup_runs_after_readiness_failure() -> None:
    session = HarnessSession.for_test(readiness=False)
    result = session.run(mode="backend-deterministic")
    assert result["summary"]["status"] == "failed"
    assert result["cleanup"] == {
        "children_terminated": True,
        "ports_closed": True,
        "session_descriptor_removed": True,
        "fixture_root_removed": True,
        "temporary_root_removed": True,
    }


def test_deterministic_chat_requires_order_privacy_and_one_terminal_event() -> None:
    session = HarnessSession.for_test(readiness=True)
    result = session.run(mode="backend-deterministic")
    statuses = {item["id"]: item["status"] for item in result["checks"]}
    assert statuses["chat.synthetic_order"] == "passed"
    assert statuses["chat.private_metadata"] == "passed"
    assert statuses["voice.synthetic_stt"] == "passed"
    assert statuses["voice.synthetic_tts"] == "passed"
```

- [ ] **Step 2: Run the session tests and verify they fail**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_harness_session.py
```

Expected: missing `HarnessSession` and v2 check aggregation.

- [ ] **Step 3: Implement `HarnessSession` resource ownership**

The session constructor allocates a unique `TemporaryDirectory`, two
`PortLease`s, a desktop bearer, a worker bearer, and a route fixture registry.
It starts the deterministic worker first and ASP.NET edge second with:

```text
RU_DESKTOP_TOKEN=<memory-only bearer>
RU_WORKER_TOKEN=<memory-only worker bearer>
RU_DATA_ROOT=<session>/data
RU_LOG_ROOT=<session>/logs
RuBackend__BindUrl=http://127.0.0.1:<edge-port>
RuBackend__WorkerUrl=ws://127.0.0.1:<worker-port>/worker/v1
NO_PROXY=127.0.0.1,localhost
```

Readiness requires the edge to answer authenticated `/settings` and both child
lifecycles to be alive. Then run auth ownership, all route registry probes,
chat order/privacy, state counts, voice STT/TTS, and WebSocket control checks.
Use bounded timeouts: 60 seconds for readiness, 15 seconds for HTTP probes,
15 seconds for chat, and 45 seconds for voice stream.

`close()` must be idempotent and run in a `finally` block. It stops the edge and
worker in reverse order, records expected/nonzero exits, verifies both leased
ports are closed, removes the session root, and returns cleanup booleans. A
probe failure never bypasses cleanup.

- [ ] **Step 4: Make the CLI a compatibility wrapper**

Replace the current 680-line orchestration body with argument parsing plus a
call to `HarnessSession.run`. Keep these options:

```text
--project-root PATH
--worker-port PORT       diagnostic override; default is dynamic
--edge-port PORT         diagnostic override; default is dynamic
--output PATH
--require-voice-audio    production runtime manual/live gate only
--benchmark-samples N    production runtime benchmark only
--deterministic-chat     alias for --mode backend-deterministic
--mode {backend-deterministic,gui-deterministic,runtime-live}
--serve-deterministic-worker
```

`--deterministic-chat` must produce schema-v2 mode `backend-deterministic`.
`gui-deterministic` keeps the owned worker/edge session alive while the typed
GUI driver runs; its callback and descriptor details are implemented in the
GUI plan. `runtime-live` retains the production-worker path.
`--require-voice-audio` remains incompatible with deterministic mode and must
fail argument validation with a clear message. `--serve-deterministic-worker`
continues to return only the worker exit code and is never treated as a full
Harness artifact run. Import the existing helper names from the new modules so
current unit tests and any local tooling do not break.

- [ ] **Step 5: Run unit tests and the real deterministic Harness**

Run sequentially:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_harness_session.py tests/test_harness_artifacts.py tests/test_harness_ports.py tests/test_harness_processes.py tests/test_harness_routes.py tests/test_harness_voice.py tests/test_aspnetcore_compatibility_tools.py
$artifact = Join-Path $env:TEMP 'ru-harness-vnext-backend.json'
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output $artifact
if ($LASTEXITCODE -ne 0) { throw "Harness failed with exit code $LASTEXITCODE." }
$json = Get-Content -Raw -LiteralPath $artifact | ConvertFrom-Json
if ($json.schema_version -ne 2) { throw 'schema_version is not 2.' }
if ($json.coverage.http.total -ne 41 -or $json.coverage.websocket.total -ne 2) { throw 'route totals changed.' }
if (-not $json.cleanup.children_terminated -or -not $json.cleanup.ports_closed -or -not $json.cleanup.temporary_root_removed) { throw 'cleanup failed.' }
```

Expected: process exit 0; 41 HTTP and 2 WebSocket totals; all eligible routes
pass; observation routes are explicit `skipped`; synthetic STT/TTS pass; wrong
bearer is rejected; child exit classifications are `expected_stop` rather than
unexplained failures; all cleanup booleans are true.

- [ ] **Step 6: Commit the session and CLI migration**

Run:

```powershell
git add -- scripts/harness/session.py scripts/aspnetcore_compatibility_harness.py tests/test_harness_session.py tests/test_aspnetcore_compatibility_tools.py
git commit -m "feat: run backend checks through owned Harness sessions"
```

## 7. Task 6: Integrate CI and verify safe artifact publication

**Files:**

- Modify: `.github/workflows/ci.yml`
- Modify: `tests/test_ci_contract.py`
- Modify: `docs/operations/verification.md`
- Modify: `README.md`
- Modify: `docs/architecture.md`

- [ ] **Step 1: Add the CI contract test first**

Extend `tests/test_ci_contract.py` with assertions for:

```python
assert "--mode backend-deterministic" in workflow
assert "actions/upload-artifact@v4" in workflow
assert "ru-harness-vnext" in workflow
```

The test must also assert that the existing route checker, Python tests, both
dotnet test projects, and GUI build remain in the workflow.

- [ ] **Step 2: Run the CI contract test and verify it fails**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_ci_contract.py
```

Expected: failure because the workflow still invokes the legacy flag and does
not upload the new artifact name.

- [ ] **Step 3: Update CI without introducing parallel heavyweight runs**

Change the Python job to invoke:

```yaml
- run: python scripts/aspnetcore_compatibility_harness.py --mode backend-deterministic --output artifacts/aspnetcore/ru-harness-vnext.json
- uses: actions/upload-artifact@v4
  if: always()
  with:
    name: ru-harness-vnext
    path: artifacts/aspnetcore/ru-harness-vnext.json
```

Keep Python tests, the route checker, and `pip check`. Do not run Python and
dotnet heavyweight gates concurrently on this repository because the Windows
workspace can exhaust pagefile memory.

- [ ] **Step 4: Update the operational documents**

In `docs/operations/verification.md`, document the exact backend command,
schema-v2 fields, dynamic-port behavior, route coverage totals, explicit
observation skips, wrong-bearer check, and expected shutdown classification.
Keep the production `--require-voice-audio` command separate and label it as a
live runtime gate. In `README.md`, add the same two-mode distinction to the
Verification section. In `docs/architecture.md`, state that the Harness owns
its temporary worker/edge session and does not change RuApp's normal ownership
of production children.

- [ ] **Step 5: Run documentation and CI checks**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_ci_contract.py tests/test_current_documentation.py
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
```

Expected: all documentation/CI contract tests pass and the route checker still
reports `Public route contract OK: 41 HTTP routes, 2 WebSocket routes`.

- [ ] **Step 6: Commit backend CI and documentation**

Run:

```powershell
git add -- .github/workflows/ci.yml tests/test_ci_contract.py docs/operations/verification.md README.md docs/architecture.md
git commit -m "ci: publish deterministic Harness vNext artifact"
```

## 8. Backend plan verification gate

After all tasks, run the heavyweight checks sequentially:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output D:\LLM\artifacts\aspnetcore\ru-harness-vnext.json
```

Expected: all commands exit 0, the artifact validates as schema 2, route totals
remain 41/2, synthetic STT/TTS and auth ownership pass, no unclassified route
is silently skipped, and cleanup reports closed ports and removed temporary
data. Report live hardware acceptance separately; this plan does not claim it.

## 9. Self-review checklist

- [ ] Every spec requirement for backend ports, credentials, child exits, route
      fixtures, deterministic voice, cleanup, privacy, artifact v2, and CI maps
      to a task above.
- [ ] The plan contains no incomplete marker or unspecified implementation step.
- [ ] `CheckResult`, `ChildExit`, `RouteSafety`, `PortLease`, and
      `HarnessSession` names remain consistent across all tasks.
- [ ] Existing dirty files are never included in the staging commands.
- [ ] Verification commands are sequential and distinguish environment/import
      failures from code/test failures.
