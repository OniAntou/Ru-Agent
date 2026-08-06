# Voice Quality and Latency Improvements Implementation Plan

Status: Tasks 1-5 implemented and verified; Task 6 live audio/latency acceptance
and measured rollout documentation remain pending.

> For agentic workers: REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

Goal: Improve OmniVoice pronunciation/prosody quality and reduce time-to-first-audio by measuring the current path, selecting a quality-safe generation configuration, and replacing whole-WAV GUI playback with ordered incremental playback.

Architecture: Keep OmniVoice and the existing single-GPU lifecycle. Add a request-scoped FastAPI voice coordinator exposed through a dedicated authenticated /voice/stream WebSocket; it requests sentence-sized WAV chunks from the local OmniVoice process, forwards them to WinUI in order, and handles cancellation and playback acknowledgements. Keep model/profile caches in the OmniVoice process and isolate playback state in a WinUI queue.

Tech Stack: Python 3.13, FastAPI WebSocket, Flask OmniVoice loopback service, PyTorch/CUDA, pytest, C#/.NET 10, WinUI 3, ClientWebSocket, Windows MediaPlayer, VTube Studio REST/WebSocket integration.

## Global Constraints

- Keep OmniVoice as the active TTS engine; do not introduce a second model in this implementation.
- Use native 1.00x speed as the quality reference; do not make librosa.time_stretch the default speed mechanism.
- Prefer sentence-sized chunks; never split inside a word and do not expose voice-control markers to the GUI as user text.
- Keep the single-GPU ownership rule: no concurrent Llama and OmniVoice loading on the 6 GB GPU.
- Use a dedicated authenticated /voice/stream WebSocket; do not reuse /chat.
- Do not persist request IDs, pace markers, timing metadata, raw audio, or transport errors in chat, history, memory, or training data.
- Preserve the saved voice selection and report synthesis failure as unavailable; never silently switch voices.
- Keep the unrelated build.bat working-tree change out of all implementation commits.

---

## File Map

Create these focused modules:

- src/mini_gpt/voice_observability.py — immutable timing records and privacy-safe event logging.
- src/mini_gpt/voice_stream.py — sentence chunk orchestration, request generations, cancellation, and stream event payloads.
- gui-winui3/Services/VoiceStreamClient.cs — authenticated WebSocket transport and frame parsing.
- gui-winui3/Services/VoicePlaybackQueue.cs — ordered WAV queue, cancellation, MediaPlayer lifecycle, and playback acknowledgements.

Modify these existing modules:

- src/mini_gpt/omnivoice_tuning.py — named per-profile/configuration selection helpers.
- src/mini_gpt/omnivoice_tts_server.py — per-request timing hooks and a testable sentence synthesis entry point.
- src/mini_gpt/routers/voice.py — authenticated /voice/stream WebSocket endpoint and coordinator integration.
- src/mini_gpt/_server_shared.py — bounded request helper and voice stream lifecycle integration.
- src/mini_gpt/tts_chunks.py — deterministic sentence chunk contract and punctuation tests.
- gui-winui3/Services/VoicePlaybackService.cs — delegate stream playback to the queue while retaining the complete-WAV test fallback during rollout.
- gui-winui3/Views/ChatView.xaml.cs — start streaming from the completed assistant reply and cancel the previous request on interruption.
- gui-winui3/Services/ApiService.cs — expose the shared authenticated endpoint/port settings needed by VoiceStreamClient.
- tests/test_omnivoice_tts_server.py, tests/test_tts_chunks.py, tests/test_server_api.py, tests/test_voice_runtime.py — Python unit and integration coverage.
- Create: gui-winui3.Tests/gui-winui3.Tests.csproj — net10.0-windows test project with xUnit, Microsoft.NET.Test.Sdk, and a project reference to RuApp.csproj.
- Create: gui-winui3.Tests/VoicePlaybackQueueTests.cs — C# queue/protocol tests.
- README.md, TODO.md — update the manual verification and new streaming command only after the implementation is accepted.

---

### Task 1: Establish the quality and latency baseline

Files:
- Modify: scripts/benchmark_tts.py
- Modify: scripts/run_omnivoice_benchmark.ps1
- Create: data/evaluations/ru_tts_quality_latency_v2.jsonl
- Modify: tests/test_benchmark_tts_cli.py

Interfaces:
- The existing benchmark CLI arguments remain supported.
- The report adds first_audio_ms, synthesis_ms, audio_duration_ms, voice_profile, config_id, and speed for every record.
- The report never stores utterance text or raw audio.

- [ ] Step 1: Add the fixed listening manifest. Include short Vietnamese, Vietnamese-English, names/numbers, punctuation, long multi-sentence, calm, sympathetic, playful, and excited examples. Store only an ID, expected category, and text in the local evaluation input; do not add generated audio to Git.

- [ ] Step 2: Write the failing report-shape test.

    def test_tts_report_records_timing_without_text(tmp_path):
        report = run_benchmark_with_fake_engine(tmp_path)
        record = report["records"][0]
        assert {"first_audio_ms", "synthesis_ms", "audio_duration_ms"} <= record.keys()
        assert "text" not in record

- [ ] Step 3: Run the focused test and verify failure.

    & D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_benchmark_tts_cli.py -q

Expected: FAIL because timing fields and the privacy assertion are not implemented.

- [ ] Step 4: Implement timing around load, warm-up, first generated audio, file write, and total synthesis. Use perf_counter(), derive audio duration from the written WAV, and serialize only numeric timings plus profile/config identifiers.

- [ ] Step 5: Run the focused tests and static checks.

    & D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_benchmark_tts_cli.py -q
    & D:\LLM\.venv\Scripts\python.exe -m compileall -q src scripts

Expected: PASS, with no generated audio or raw utterance text in the report.

- [ ] Step 6: Commit the baseline instrumentation.

    git add scripts/benchmark_tts.py scripts/run_omnivoice_benchmark.ps1 data/evaluations/ru_tts_quality_latency_v2.jsonl tests/test_benchmark_tts_cli.py
    git commit -m "test: add voice quality and latency baseline"

### Task 2: Make OmniVoice configuration and synthesis measurable

Files:
- Modify: src/mini_gpt/omnivoice_tuning.py
- Modify: src/mini_gpt/omnivoice_tts_server.py
- Test: tests/test_omnivoice_tuning.py
- Test: tests/test_omnivoice_tts_server.py

Interfaces:
- Add settings_for_profile(profile_id: str, config_id: str | None = None) -> OmniVoiceGenerationSettings.
- Add synthesize_fast(text: str, profile: str = "ru", speed: float = DEFAULT_VOICE_SPEED, *, timing: Callable[[str, float], None] | None = None) -> str.
- Keep get_gui_trial_settings() as a compatibility wrapper returning the selected default profile configuration.

- [ ] Step 1: Add failing configuration tests. Assert that settings_for_profile("ru") and settings_for_profile("ai_vy") return the selected 16-step Vietnamese-normalized configuration, that all four known config IDs resolve, and that an unknown profile/config raises ValueError.

    def test_settings_for_profile_resolves_known_configuration():
        assert settings_for_profile("ru", "vi-normalized-20").num_step == 20

    def test_settings_for_profile_rejects_unknown_config():
        with pytest.raises(ValueError, match="Unsupported OmniVoice configuration"):
            settings_for_profile("ru", "unknown")

- [ ] Step 2: Run the focused tuning tests and verify failure.

    & D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_omnivoice_tuning.py -q

- [ ] Step 3: Implement the named settings lookup and keep the current GUI trial as the default. Do not change the persisted voice label or speed bounds.

- [ ] Step 4: Add synthesis timing callbacks. Emit only warmup_ms, generate_ms, write_ms, and total_ms; preserve the existing cached model and per-profile clone prompt behavior. Keep speed 1.00x as the no-post-processing path.

- [ ] Step 5: Add failing tests for timing and configuration propagation. Extend the fake model test to assert the chosen language, normalize_text, num_step, and timing callback names.

- [ ] Step 6: Run the focused tests.

    & D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_omnivoice_tuning.py tests/test_omnivoice_tts_server.py -q

Expected: PASS, including existing model-cache, waveform-shape, error, and HTTP contract tests.

- [ ] Step 7: Commit the measurable OmniVoice adapter.

    git add src/mini_gpt/omnivoice_tuning.py src/mini_gpt/omnivoice_tts_server.py tests/test_omnivoice_tuning.py tests/test_omnivoice_tts_server.py
    git commit -m "feat: expose measurable OmniVoice configurations"

### Task 3: Define the voice stream protocol and coordinator

Files:
- Create: src/mini_gpt/voice_observability.py
- Create: src/mini_gpt/voice_stream.py
- Modify: src/mini_gpt/routers/voice.py
- Modify: src/mini_gpt/_server_shared.py
- Modify: src/mini_gpt/tts_chunks.py
- Test: tests/test_tts_chunks.py
- Test: tests/test_server_api.py
- Create: tests/test_voice_stream.py

Interfaces:
- VoiceStreamEvent is a dataclass with event_type in started/audio/ended/cancelled/failed, request_id: str, sequence: int, and optional sample_rate: int, audio_format: str, error_code: str.
- VoiceStreamCoordinator.start(request_id: str, text: str, voice_label: str, speed: float) -> AsyncIterator[VoiceStreamEvent] yields ordered sentence audio events.
- VoiceStreamCoordinator.cancel(request_id: str) -> None invalidates future events for that request.
- POST /voice/tts remains available as the complete-WAV compatibility path.
- WebSocket /voice/stream accepts start and cancel JSON frames.

- [ ] Step 1: Add failing sentence-splitting tests. Cover punctuation, newlines, maximum length, no word splitting when a space exists, deterministic sequence order, empty text, and marker removal before chunking.

    def test_split_tts_chunks_preserves_sentence_order_and_word_boundaries():
        chunks = split_tts_chunks("Một câu ngắn. Đây là câu thứ hai rất dài.", max_chars=24)
        assert chunks == ["Một câu ngắn.", "Đây là câu thứ hai", "rất dài."]

- [ ] Step 2: Run the focused chunk tests and verify failure.

    & D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_tts_chunks.py -q

- [ ] Step 3: Add immutable timing records and privacy-safe event serialization. VoiceTiming must accept only numeric timings and identifiers; its serialization must not accept or emit spoken text or audio bytes.

- [ ] Step 4: Implement the coordinator with a monotonically increasing generation per active stream. Acquire the existing memory turn lock and GPU speech lifecycle before the first chunk, call the existing local /tts helper once per sentence, check cancellation before each next sentence, and release the lock in finally.

- [ ] Step 5: Implement the authenticated WebSocket route. Reuse the existing desktop token validation pattern used by /chat; reject missing/invalid tokens, reject malformed start/cancel frames, send a control frame before each binary frame with request_id, sequence, sample_rate=24000, and format="wav", then send ended, cancelled, or failed.

- [ ] Step 6: Add API tests for the protocol. Test unauthorized connection, malformed start, one fake two-sentence stream, ordered sequence numbers, cancellation after the first chunk, and no leaked lock after synthesis failure.

    def test_voice_stream_sends_ordered_audio_and_end_event(monkeypatch):
        fake_voice_chunks(monkeypatch, [b"RIFF-1", b"RIFF-2"])
        with AuthenticatedTestClient(server.app) as client:
            with client.websocket_connect("/voice/stream?token=desktop-secret") as ws:
                ws.send_json({"type": "start", "request_id": "r1", "text": "A. B."})
                assert ws.receive_json() == {"type": "started", "request_id": "r1"}
                assert ws.receive_json()["sequence"] == 0
                assert ws.receive_bytes() == b"RIFF-1"
                assert ws.receive_json()["sequence"] == 1
                assert ws.receive_bytes() == b"RIFF-2"
                assert ws.receive_json() == {"type": "ended", "request_id": "r1"}

- [ ] Step 7: Run the focused backend suite.

    & D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_tts_chunks.py tests/test_voice_stream.py tests/test_server_api.py -q

Expected: PASS with no stale events and no leaked lock.

- [ ] Step 8: Commit the backend stream protocol.

    git add src/mini_gpt/voice_observability.py src/mini_gpt/voice_stream.py src/mini_gpt/routers/voice.py src/mini_gpt/_server_shared.py src/mini_gpt/tts_chunks.py tests/test_tts_chunks.py tests/test_voice_stream.py tests/test_server_api.py
    git commit -m "feat: stream voice chunks over authenticated websocket"

### Task 4: Implement the WinUI transport and playback queue

Files:
- Create: gui-winui3/Services/VoiceStreamClient.cs
- Create: gui-winui3/Services/VoicePlaybackQueue.cs
- Modify: gui-winui3/Services/VoicePlaybackService.cs
- Modify: gui-winui3/Services/ApiService.cs
- Modify: gui-winui3/Views/ChatView.xaml.cs
- Create: gui-winui3.Tests/gui-winui3.Tests.csproj
- Test: gui-winui3.Tests/VoicePlaybackQueueTests.cs

Interfaces:
- VoiceStreamClient.StartAsync(string requestId, string text, string voiceLabel, double speed, Func<VoiceAudioChunk, Task> onChunk, Func<VoiceStreamTerminalEvent, Task> onTerminal, CancellationToken cancellationToken) opens the authenticated /voice/stream socket and invokes callbacks in frame order.
- VoiceStreamClient.CancelAsync(string requestId) sends the cancel control frame and closes only the matching stream.
- VoicePlaybackQueue.EnqueueAsync(string requestId, VoiceAudioChunk chunk, CancellationToken cancellationToken) accepts only the active request generation.
- VoicePlaybackQueue.Cancel(string requestId) disposes pending streams and sends VTS speaking=false.
- VoicePlaybackQueue.Completed and VoicePlaybackQueue.Failed carry the request ID and terminal reason for acknowledgement.

- [ ] Step 1: Add failing C# protocol tests. Test JSON start/cancel serialization, binary frame association with the preceding sequence control frame, stale request rejection, sequence gap rejection, and cancellation disposal.

- [ ] Step 2: Create the GUI test project and verify the new tests fail before implementation. Target net10.0-windows10.0.26100.0, reference gui-winui3/RuApp.csproj, and add Microsoft.NET.Test.Sdk, xunit, and xunit.runner.visualstudio. Run:

    dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug

Expected: FAIL because the stream client and queue do not exist.

- [ ] Step 3: Implement VoiceStreamClient with ClientWebSocket. Set the desktop token query parameter exactly as ChatWebSocketClient does, use a receive loop that handles complete text and binary messages, reject an audio frame without a preceding audio control frame, and stop on terminal events or cancellation.

- [ ] Step 4: Implement VoicePlaybackQueue. Maintain one active request ID, a bounded Channel<VoiceAudioChunk>, and one media-player owner. For each chunk, create an InMemoryRandomAccessStream, start the first valid chunk only after MediaOpened, dispose the stream after MediaEnded/MediaFailed, and never allow an older request to call Play.

- [ ] Step 5: Integrate the queue into VoicePlaybackService. Keep SpeakTextAsync as the public entry point; use the stream path by default behind a private feature flag, retain complete-WAV fallback for a stream connection failure, and ensure StopVoice() cancels the request before clearing player/VTS state.

- [ ] Step 6: Integrate completed chat replies. In ChatView.OnWsDoneReceived, strip private markers once, create a new request ID, cancel the previous voice request, and call SpeakTextAsync with the visible reply. Do not send stream control frames to the chat display.

- [ ] Step 7: Run the GUI tests and Debug build.

    dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug
    dotnet build gui-winui3\RuApp.csproj -c Debug -r win-x64 --no-restore

Expected: PASS with zero compiler warnings/errors and no stale playback test failures.

- [ ] Step 8: Commit the WinUI stream queue.

    git add gui-winui3/Services/VoiceStreamClient.cs gui-winui3/Services/VoicePlaybackQueue.cs gui-winui3/Services/VoicePlaybackService.cs gui-winui3/Services/ApiService.cs gui-winui3/Views/ChatView.xaml.cs gui-winui3.Tests
    git commit -m "feat: play voice responses incrementally in WinUI"

### Task 5: Add lifecycle acknowledgements and GPU cleanup verification

Files:
- Modify: src/mini_gpt/voice_stream.py
- Modify: src/mini_gpt/routers/voice.py
- Modify: gui-winui3/Services/VoicePlaybackQueue.cs
- Modify: gui-winui3/Services/VoicePlaybackService.cs
- Modify: tests/test_gpu_lifecycle.py
- Modify: tests/test_server_api.py
- Test: gui-winui3.Tests/VoicePlaybackQueueTests.cs

Interfaces:
- GUI sends {"type":"playback","request_id":"...","state":"started|ended|cancelled|failed"}.
- Backend records the acknowledgement in privacy-safe timing logs and ignores acknowledgements for inactive request generations.
- VoicePlaybackQueue resets VTS speaking state on every terminal media event, including cancellation and media failure.

- [ ] Step 1: Add failing lifecycle tests. Cover warm-up failure retaining Llama state, release after voice completion, cancellation releasing the memory turn lock, duplicate terminal events, and VTS reset on MediaFailed.

- [ ] Step 2: Run lifecycle tests and verify failure.

    & D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_gpu_lifecycle.py tests/test_server_api.py -q

- [ ] Step 3: Implement acknowledgement handling. The backend must accept only the active request ID, mark the terminal timestamp once, and discard late acknowledgements. The GUI must send failed when media cannot open and cancelled when StopVoice() wins the race.

- [ ] Step 4: Run Python, GUI, and build gates.

    & D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_gpu_lifecycle.py tests/test_server_api.py -q
    dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug
    dotnet build gui-winui3\RuApp.csproj -c Release -r win-x64 --no-restore

- [ ] Step 5: Commit lifecycle hardening.

    git add src/mini_gpt/voice_stream.py src/mini_gpt/routers/voice.py gui-winui3/Services/VoicePlaybackQueue.cs gui-winui3/Services/VoicePlaybackService.cs tests/test_gpu_lifecycle.py tests/test_server_api.py gui-winui3.Tests
    git commit -m "fix: acknowledge and clean up voice playback lifecycle"

### Task 6: Quality-safe rollout and documentation

Files:
- Modify: README.md
- Modify: TODO.md
- Modify: the design spec only if an accepted implementation decision changes it.
- Test: tests/test_voice_runtime.py, tests/test_voice_pacing.py, tests/test_benchmark_tts_cli.py

- [ ] Step 1: Run the configuration benchmark for both profiles. Use the fixed manifest and all four settings at native 1.00x. Record cold and warm p50/p95, first-audio timing, and blind listening scores in eval_results/omnivoice outside Git-tracked source files.

    & D:\LLM\scripts\run_omnivoice_benchmark.ps1 -Preflight

After confirming the existing local model/cache are present, run:

    & D:\LLM\scripts\run_omnivoice_benchmark.ps1 -ConfirmModelDownload

- [ ] Step 2: Select the configuration only when pronunciation and naturalness are non-inferior. Keep vi-normalized-16 as the fallback if 20/24 steps do not improve listening scores enough to justify their latency.

- [ ] Step 3: Run the real manual acceptance gate. With VTube Studio open, verify both profiles, Vietnamese-English, long replies, excited/calm delivery, interruption, restart persistence, VoiceVolume lip-sync, audio actually played, and no leftover voice process after ten warm/cancel/release cycles.

- [ ] Step 4: Update README and TODO with measured results only. Document the stream command/protocol only at user-relevant level, retain the existing no-metadata and reference-audio rules, and mark manual gates complete only when actual audio and VTube behavior are confirmed.

- [ ] Step 5: Run the complete verification suite.

    & D:\LLM\.venv\Scripts\python.exe -m pytest -q
    & D:\LLM\.venv\Scripts\python.exe -m compileall -q src scripts tests
    & D:\LLM\.venv\Scripts\python.exe -m pip check
    dotnet build gui-winui3\RuApp.csproj -c Debug -r win-x64 --no-restore
    dotnet build gui-winui3\RuApp.csproj -c Release -r win-x64 --no-restore
    & D:\LLM\build.bat
    git diff --check

- [ ] Step 6: Check all project documents before handoff. Confirm README/TODO and the spec contain only measured claims, remove no unrelated documents, and leave the existing build.bat change either explicitly committed in its own requested change or unchanged in the working tree.

- [ ] Step 7: Commit the accepted rollout documentation.

    git add README.md TODO.md
    git commit -m "docs: record accepted voice streaming rollout"

---

## Plan self-review

- Spec coverage: baseline measurement, configuration comparison, true incremental delivery, cancellation, playback acknowledgement, GPU ownership, privacy, quality listening, latency percentiles, manual acceptance, and documentation are covered by Tasks 1–6.
- Interface consistency: Python uses VoiceStreamEvent and VoiceStreamCoordinator.start/cancel; C# uses VoiceStreamClient.StartAsync/CancelAsync and VoicePlaybackQueue.EnqueueAsync/Cancel; the protocol uses the same request_id, sequence, and terminal event names throughout.
- Privacy: reports contain timings and identifiers only; WebSocket control metadata is never sent to chat/history/memory; raw audio remains transient.
- Rollout safety: the complete-WAV endpoint remains available and the GUI stream path has a fallback until the real-audio acceptance gate passes.
- No model replacement, concurrent GPU loading, or unmeasured quality claim is introduced.
