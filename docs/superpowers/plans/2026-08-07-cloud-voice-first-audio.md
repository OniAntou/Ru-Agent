# Cloud Voice First-Audio Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce hosted Ru chat time-to-first-audio without changing the selected cloud provider or degrading OmniVoice quality.

**Architecture:** The FastAPI backend schedules a deduplicated, non-blocking OmniVoice warm-up only when hosted chat and auto-read are active. The WinUI reply buffer emits a single opening phrase at a word-safe boundary when no first sentence is available, then returns to sentence-first output. A pure GUI tracker reports numeric-only milestones to an authenticated loopback endpoint, which logs no user text or identifiers.

**Tech Stack:** Python 3.13, FastAPI/Pydantic v2, WinUI 3/.NET 10, xUnit, pytest, local OmniVoice over loopback HTTP/WebSocket.

## Global Constraints

- Preserve the user-owned `build.bat` change; never stage or modify it.
- Keep `share_memory_with_cloud` true by default and do not include it in telemetry.
- Do not change Kilo/OpenCode model selection, prompt, voice profile, OmniVoice generation configuration, or voice speed.
- Never send telemetry to a cloud provider or persist reply text, raw audio, request IDs, VTS markers, or VOICE_PACE metadata.
- Use only one heavyweight GPU runtime at a time on the 6,141 MiB RTX 4050; local GGUF retains its existing release-before-generation boundary.
- Run heavyweight Python/C# verification sequentially to avoid Windows pagefile error 1455.
- Stage only files in this plan and update README/TODO with implemented facts only.

---

## File map

- `src/mini_gpt/_server_shared.py` — cloud-warm eligibility, deduplicated daemon scheduler, and lifespan scheduling.
- `src/mini_gpt/routers/settings.py` — schedule the same non-blocking warm-up after voice or hosted settings save.
- `src/mini_gpt/routers/voice.py` — strict, authenticated numeric latency-report endpoint.
- `tests/test_server_api.py` — scheduler and latency-endpoint behavior.
- `gui-winui3/Services/VoiceReplySentenceBuffer.cs` — pure one-time early phrase selection.
- `gui-winui3/Services/VoiceReplyLatencyTracker.cs` — pure timestamp-to-numeric-snapshot conversion.
- `gui-winui3/Services/VoicePlaybackService.cs` — mark reply milestones and submit one best-effort report.
- `gui-winui3/Services/ApiService.cs` — authenticated `POST /voice/latency` transport.
- `gui-winui3.Tests/VoicePlaybackQueueTests.cs` — phrase-buffer regression tests.
- `gui-winui3.Tests/VoiceReplyLatencyTrackerTests.cs` — telemetry privacy and timing tests.
- `README.md`, `TODO.md`, and `docs/superpowers/specs/2026-08-07-cloud-voice-first-audio-design.md` — final implemented behavior and remaining live acceptance gates.

## Task 1: Schedule a cloud-only OmniVoice warm-up

**Files:**

- Modify: `src/mini_gpt/_server_shared.py:111-126, 615-629, 976-982`
- Modify: `src/mini_gpt/routers/settings.py:1-20, 89-165`
- Modify: `tests/test_server_api.py`

**Interfaces:**

- Produce `_should_warm_voice_for_cloud(settings: dict[str, object]) -> bool`.
- Produce `_schedule_cloud_voice_warmup() -> bool`; `True` means it started one background worker, `False` means eligibility failed or a worker is already active.
- The worker calls `GpuLifecycle.warm_voice_for_cloud(selection, set_status)` exactly once and logs success/failure locally.
- The settings endpoint always returns its existing response shape without awaiting model warm-up.

- [x] **Step 1: Write failing scheduler tests**

  Add focused tests that monkeypatch the runner backend, effective settings, selected voice, lifecycle, and thread construction. Cover one hosted + auto-read OmniVoice request, local GGUF, auto-read disabled, and duplicate schedule calls:

  ```python
  def test_schedule_cloud_voice_warmup_starts_once_for_hosted_auto_read(monkeypatch):
      class DeferredThread:
          created: list["DeferredThread"] = []

          def __init__(self, *, target, name, daemon):
              self.target = target
              self.name = name
              self.daemon = daemon
              self.started = False
              self.created.append(self)

          def start(self):
              self.started = True

      events: list[object] = []
      monkeypatch.setattr(server_shared.core.runner, "backend", BACKEND_KILO)
      monkeypatch.setattr(server_shared, "effective_settings", lambda: {"auto_read_replies": True})
      monkeypatch.setattr(
          server_shared,
          "_voice_selection",
          lambda: VoiceSelection("Ru (OmniVoice)", "omnivoice", "ru"),
      )
      monkeypatch.setattr(
          server_shared,
          "_gpu_lifecycle",
          lambda: type("Lifecycle", (), {
              "warm_voice_for_cloud": lambda self, selection, status: (
                  status("Warming OmniVoice voice..."),
                  events.append(("warm", selection.profile_id)),
                  0.0,
              )[-1],
          })(),
      )
      monkeypatch.setattr(server_shared.threading, "Thread", DeferredThread)

      assert server_shared._schedule_cloud_voice_warmup() is True
      assert server_shared._schedule_cloud_voice_warmup() is False
      DeferredThread.created[0].target()
      assert events == [
          ("status", "Warming OmniVoice voice..."),
          ("warm", "ru"),
      ]

  @pytest.mark.parametrize("backend,auto_read", [
      (BACKEND_GGUF, True),
      (BACKEND_KILO, False),
  ])
  def test_schedule_cloud_voice_warmup_skips_ineligible_runtime(monkeypatch, backend, auto_read):
      monkeypatch.setattr(server_shared.core.runner, "backend", backend)
      monkeypatch.setattr(server_shared, "effective_settings", lambda: {"auto_read_replies": auto_read})
      monkeypatch.setattr(
          server_shared,
          "_voice_selection",
          lambda: VoiceSelection("Ru (OmniVoice)", "omnivoice", "ru"),
      )
      assert server_shared._schedule_cloud_voice_warmup() is False
  ```

- [x] **Step 2: Run the new tests and verify RED**

  Run:

  ```powershell
  & D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_server_api.py -q
  ```

  Expected: FAIL because `_schedule_cloud_voice_warmup` does not exist.

- [x] **Step 3: Implement the smallest scheduler in `_server_shared.py`**

  Add `auto_read_replies: True` to `_DEFAULT_SETTINGS`. Add a module-level
  non-blocking lock and helper functions with this exact policy:

  ```python
  def _should_warm_voice_for_cloud(settings: dict[str, object]) -> bool:
      return (
          core.runner.backend in (BACKEND_OPENCODE, BACKEND_KILO)
          and settings.get("auto_read_replies", True) is True
          and _voice_selection().engine_id == "omnivoice"
      )

  def _schedule_cloud_voice_warmup() -> bool:
      if not _should_warm_voice_for_cloud(effective_settings()):
          return False
      if not _CLOUD_VOICE_WARMUP_LOCK.acquire(blocking=False):
          return False
      selection = _voice_selection()
      def warm() -> None:
          try:
              _gpu_lifecycle().warm_voice_for_cloud(selection, logger.info)
          except Exception:
              logger.exception("Background cloud voice warm-up failed")
          finally:
              _CLOUD_VOICE_WARMUP_LOCK.release()
      threading.Thread(target=warm, name="cloud-voice-warmup", daemon=True).start()
      return True
  ```

  Call `_schedule_cloud_voice_warmup()` after `_apply_settings_on_startup()` in
  `_application_lifespan`. Do not await it and do not invoke it for GGUF.

- [x] **Step 4: Trigger the scheduler after relevant settings saves**

  Import `_schedule_cloud_voice_warmup` in `routers/settings.py`. Call it after
  `_merge_settings(_apply)` on a voice-only save and after a successful
  `core.runner.load(...)` for a hosted backend. Do not call it when loading
  fails; preserve the current settings response and error handling.

- [x] **Step 5: Run focused backend tests and verify GREEN**

  Run:

  ```powershell
  & D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gpu_lifecycle.py tests\test_server_api.py -q
  ```

  Expected: PASS. The tests prove one eligible background warm-up, no startup
  blocking, no warm-up in local mode, and preserved lifecycle rollback.

- [x] **Step 6: Commit the isolated backend lifecycle change**

  ```powershell
  git add src/mini_gpt/_server_shared.py src/mini_gpt/routers/settings.py tests/test_server_api.py
  git commit -m "perf: warm OmniVoice for hosted chat"
  ```

## Task 2: Emit one safe first phrase before sentence punctuation

**Files:**

- Modify: `gui-winui3/Services/VoiceReplySentenceBuffer.cs:7-76`
- Modify: `gui-winui3.Tests/VoicePlaybackQueueTests.cs:84-93`

**Interfaces:**

- `VoiceReplySentenceBuffer.Append(string text)` still returns only complete,
  ordered speech units.
- A complete sentence always wins over phrase splitting.
- The buffer may emit exactly one first phrase when it finds the first valid
  boundary in 48-60 characters, or the first valid boundary in 61-72 characters.
- `Flush()` emits all remaining text exactly once.

- [x] **Step 1: Write failing first-phrase tests**

  Add these tests to `VoicePlaybackQueueTests` using a long, unpunctuated
  Vietnamese opening whose selected phrase ends at a whitespace after character
  48. The test must assert the whole returned phrase rather than only its
  length:

  ```csharp
  [Fact]
  public void SentenceBufferEmitsOneWordSafeOpeningPhraseWithoutPunctuation()
  {
      var buffer = new VoiceReplySentenceBuffer();

      Assert.Equal(
          new[] { "Dạ anh, em sẽ kiểm tra kỹ phần quan trọng này trước" },
          buffer.Append("Dạ anh, em sẽ kiểm tra kỹ phần quan trọng này trước rồi báo lại cho anh ngay khi có kết quả"));
  }

  [Fact]
  public void SentenceBufferReturnsToSentenceBoundariesAfterOpeningPhrase()
  {
      var buffer = new VoiceReplySentenceBuffer();
      buffer.Append("Dạ anh, em sẽ kiểm tra kỹ phần quan trọng này trước rồi báo lại cho anh ngay khi có kết quả");

      Assert.Empty(buffer.Append(" và không để sót điểm nào"));
      Assert.Equal(
          new[] { "rồi báo lại cho anh ngay khi có kết quả và không để sót điểm nào." },
          buffer.Append("."));
  }
  ```

  Also keep the existing completed-sentence and final-flush regression test.

- [x] **Step 2: Run the GUI test project and verify RED**

  Run:

  ```powershell
  dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug --no-restore
  ```

  Expected: FAIL because an unpunctuated opening currently remains buffered.

- [x] **Step 3: Add minimal phrase selection to `VoiceReplySentenceBuffer`**

  Add private constants and state:

  ```csharp
  private const int FirstPhraseMinimumChars = 48;
  private const int FirstPhrasePreferredChars = 60;
  private const int FirstPhraseMaximumChars = 72;
  private bool _hasEmittedFirstSpeechUnit;
  ```

  Keep `FindBoundary()` unchanged for full sentences. In `Drain(false)`, first
  drain any full sentence. If none exists and no speech unit has been emitted,
  call a new `FindFirstPhraseBoundary()` that returns a separator only after a
  complete word. It must search the first valid separator in 48-60, then the
  first valid separator in 61-72. `AddSentence(...)` sets
  `_hasEmittedFirstSpeechUnit = true` whenever it returns non-empty output.
  Never add a timer, never split a token, and do not emit a second phrase before
  sentence punctuation.

- [x] **Step 4: Run GUI tests and build to verify GREEN**

  Run:

  ```powershell
  dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug --no-restore
  dotnet build gui-winui3\RuApp.csproj -c Debug -r win-x64 --no-restore
  ```

  Expected: PASS. Existing sentence, decimal, queue-order, and cancellation
  behavior remains unchanged.

- [x] **Step 5: Commit the speech-boundary change**

  ```powershell
  git add gui-winui3/Services/VoiceReplySentenceBuffer.cs gui-winui3.Tests/VoicePlaybackQueueTests.cs
  git commit -m "perf: start reply voice at safe phrase boundaries"
  ```

## Task 3: Add private end-to-end latency telemetry

**Files:**

- Create: `gui-winui3/Services/VoiceReplyLatencyTracker.cs`
- Create: `gui-winui3.Tests/VoiceReplyLatencyTrackerTests.cs`
- Modify: `gui-winui3/Services/VoicePlaybackService.cs:20-31, 159-465`
- Modify: `gui-winui3/Services/ApiService.cs:207-260`
- Modify: `src/mini_gpt/routers/voice.py:1-42, 213-242`
- Modify: `tests/test_server_api.py`

**Interfaces:**

- `VoiceReplyLatencyTracker` accepts only monotonic timestamps and exposes
  `MarkFirstChatDelta()`, `MarkFirstPhraseQueued()`, `MarkFirstAudioReceived()`,
  `MarkPlaybackRequested()`, and `TryComplete(VoiceReplyOutcome outcome)`.
- `TryComplete` returns `null` after the first terminal snapshot.
- `VoiceReplyLatencySnapshot` contains only nullable `double` durations and
  one fixed outcome; it has no text or identifier property.
- `ApiService.ReportVoiceLatencyAsync(VoiceReplyLatencySnapshot snapshot)` is
  best-effort and never throws into playback.
- `POST /voice/latency` accepts only validated numeric durations in [0, 120000]
  and `outcome` in `completed|cancelled|failed`.

- [x] **Step 1: Write failing pure C# tracker tests**

  Create `VoiceReplyLatencyTrackerTests.cs` with an injected timestamp source
  so no test sleeps. Cover ordered milestones, first-value-wins behavior,
  terminal idempotence, and the no-text wire shape:

  ```csharp
  [Fact]
  public void TrackerBuildsNumericOnlySnapshot()
  {
      long[] ticks = [0, 10, 20, 45, 60];
      int index = 0;
      var tracker = new VoiceReplyLatencyTracker(() => ticks[index++], ticksPerSecond: 1_000);
      tracker.MarkFirstChatDelta();
      tracker.MarkFirstPhraseQueued();
      tracker.MarkFirstAudioReceived();
      tracker.MarkPlaybackRequested();

      VoiceReplyLatencySnapshot snapshot = Assert.IsType<VoiceReplyLatencySnapshot>(
          tracker.TryComplete(VoiceReplyOutcome.Completed));
      Assert.Equal(VoiceReplyOutcome.Completed, snapshot.Outcome);
      string payload = JsonSerializer.Serialize(snapshot.ToWirePayload());
      Assert.DoesNotContain("text", payload, StringComparison.OrdinalIgnoreCase);
  }
  ```

  Use a valid monotonic delegate/index implementation in the final test; avoid
  sleeping and do not introduce a reply text parameter.

- [x] **Step 2: Run the C# tests and verify RED**

  Run:

  ```powershell
  dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug --no-restore
  ```

  Expected: FAIL because the tracker types do not exist.

- [x] **Step 3: Implement the pure tracker**

  Create a record plus class that uses `Stopwatch.GetTimestamp` and
  `Stopwatch.Frequency` by default. Each `Mark...` method records only its first
  timestamp. `TryComplete(...)` converts recorded ticks to rounded milliseconds,
  sets one terminal state under a lock, and returns `null` for later calls.
  `ToWirePayload()` returns an anonymous/dictionary shape with only the five
  approved fields.

- [x] **Step 4: Add failing FastAPI contract tests**

  Add an authenticated endpoint test with a valid payload and negative cases
  for a raw `text` field, an unknown field, a negative duration, a duration
  greater than 120000, and `NaN`:

  ```python
  def test_voice_latency_accepts_only_numeric_private_milestones(client, caplog):
      response = client.post("/voice/latency", json={
          "submit_to_first_delta_ms": 120.0,
          "submit_to_first_phrase_ms": 380.0,
          "phrase_to_first_audio_ms": 1140.0,
          "submit_to_playback_requested_ms": 1580.0,
          "outcome": "completed",
      })
      assert response.status_code == 200
      assert "text" not in caplog.text.lower()

  def test_voice_latency_rejects_text_payload(client):
      assert client.post("/voice/latency", json={"outcome": "completed", "text": "secret"}).status_code == 422
  ```

- [x] **Step 5: Run the endpoint tests and verify RED**

  Run:

  ```powershell
  & D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_server_api.py -q
  ```

  Expected: FAIL with route-not-found or absent strict model validation.

- [x] **Step 6: Implement the strict loopback endpoint**

  In `routers/voice.py`, add:

  ```python
  class VoiceLatencyPayload(BaseModel):
      model_config = ConfigDict(extra="forbid")
      submit_to_first_delta_ms: Annotated[FiniteFloat | None, Field(default=None, ge=0, le=120_000)]
      submit_to_first_phrase_ms: Annotated[FiniteFloat | None, Field(default=None, ge=0, le=120_000)]
      phrase_to_first_audio_ms: Annotated[FiniteFloat | None, Field(default=None, ge=0, le=120_000)]
      submit_to_playback_requested_ms: Annotated[FiniteFloat | None, Field(default=None, ge=0, le=120_000)]
      outcome: Literal["completed", "cancelled", "failed"]
  ```

  Log `payload.model_dump(exclude_none=True)` with a fixed telemetry prefix and
  return `{"status": "ok"}`. Do not store it in chat/history/memory and do not
  add a cloud request.

- [x] **Step 7: Wire the tracker into reply playback**

  In `VoicePlaybackService`:

  - create the tracker in `BeginReplyStream()` before the channel starts;
  - mark first delta in `AppendReplyDelta()`;
  - mark first phrase when the buffer returns its first non-empty unit;
  - mark first audio inside the first stream chunk callback or complete-WAV
    fallback enqueue;
  - mark playback requested immediately before the first reply `PlayWavAsync`;
  - report `completed` after `CompleteReplyStreamAsync`, `cancelled` in
    `StopVoice()`, and `failed` when the reply worker fails before audio;
  - clear the tracker under `_replyGate` and make every report best-effort.

  In `ApiService`, post `snapshot.ToWirePayload()` to `/voice/latency`, catch
  all transport exceptions, write only the exception type/message to Debug, and
  return `false` instead of affecting playback.

- [x] **Step 8: Run focused Python and C# tests to verify GREEN**

  Run sequentially:

  ```powershell
  & D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_server_api.py -q
  dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug --no-restore
  dotnet build gui-winui3\RuApp.csproj -c Debug -r win-x64 --no-restore
  ```

  Expected: PASS. The tracker reports once, endpoint rejects non-private
  payloads, and normal reply playback still compiles.

- [x] **Step 9: Commit telemetry without content**

  ```powershell
  git add gui-winui3/Services/VoiceReplyLatencyTracker.cs gui-winui3/Services/VoicePlaybackService.cs gui-winui3/Services/ApiService.cs gui-winui3.Tests/VoiceReplyLatencyTrackerTests.cs src/mini_gpt/routers/voice.py tests/test_server_api.py
  git commit -m "feat: record private reply voice latency"
  ```

## Task 4: Verify the integrated runtime and update project documents

**Files:**

- Modify: `README.md`
- Modify: `TODO.md`
- Modify: `docs/superpowers/specs/2026-08-07-cloud-voice-first-audio-design.md`
- Modify: `docs/superpowers/plans/2026-08-07-cloud-voice-first-audio.md`
- Test: `tests/`, `gui-winui3.Tests/`, published GUI output

**Interfaces:**

- README describes auto-warm, one safe opening phrase, and numeric-only local
  latency logs without claiming a live cloud latency improvement.
- TODO keeps manual listening and provider A/B acceptance pending until actual
  audio and telemetry evidence exist.

- [x] **Step 1: Run the full automated suite sequentially**

  Run:

  ```powershell
  & D:\LLM\.venv\Scripts\python.exe -m pytest -q
  dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug --no-restore
  cmd /c build.bat
  git diff --check
  ```

  Expected: Python suite, GUI tests, and self-contained publish succeed; no
  whitespace errors. Report environment failures separately from code failures.

- [x] **Step 2: Run a local no-cloud latency smoke probe**

  Start only the temporary OmniVoice loopback service, warm it once, and use a
  representative first phrase to confirm a warm voice request still reaches its
  first WAV in the previously measured 1-2 second range. Stop the temporary
  service and confirm port 5052 and its GPU allocation are released afterward.

- [x] **Step 3: Update docs with implemented facts only**

  Update README and TODO to state:

  - background warm-up applies only to hosted auto-read;
  - the opening phrase may be emitted once at a word-safe boundary;
  - telemetry is numeric-only and loopback-local;
  - live cloud p50/p95 and provider A/B remain pending user-observed evidence.

  Change the design spec status to `Implemented; live cloud acceptance and
  provider A/B pending` only after all automated gates pass.

- [x] **Step 4: Re-check project documentation**

  Run:

  ```powershell
  rg -n -i "kilo-auto/free|sentence|phrase|warm|latency|telemetry" README.md TODO.md docs\superpowers\specs\2026-08-07-cloud-voice-first-audio-design.md
  git diff --check
  ```

  Remove no document unless it is demonstrably obsolete; preserve the prior
  voice-quality design as historical Phase 1 context.

- [x] **Step 5: Commit final code/documentation scope**

  ```powershell
  git add README.md TODO.md docs/superpowers/specs/2026-08-07-cloud-voice-first-audio-design.md docs/superpowers/plans/2026-08-07-cloud-voice-first-audio.md
  git commit -m "docs: record cloud voice latency rollout"
  ```

## Plan self-review

- Spec coverage: Tasks 1-3 implement the approved warm-up, phrase-first, and
  private telemetry changes; Task 4 verifies and documents them.
- Privacy coverage: no task accepts or logs speech text, raw audio, a request
  ID, or VTS metadata.
- GPU coverage: warm-up is hosted-only and uses the existing lifecycle lock;
  local GGUF does not schedule it.
- Test coverage: every new production seam has a RED-first focused test, then
  a GREEN verification and an integrated final gate.
- Scope coverage: provider/model A/B is explicitly excluded and left for the
  next approved task after measured telemetry is available.
