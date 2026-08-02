# DeepSeek Handoff — Ru Local Screen Awareness

**Checkpoint:** 2026-08-02

**Repository:** `D:\LLM`

**Branch:** `main`

**Implementation baseline:** `2bee851` (`fix: harden screen reaction latency`)

**Handoff revision:** use the current `main` HEAD containing this document

**Remote:** `origin/main` is synchronized after this handoff is pushed

**Worktree at handoff:** clean
**Runtime at handoff:** stopped; no listeners on 1420, 8000, 5052, or 18111

## Read this first

The feature is implemented but **not accepted yet**. Local capture, Qwen vision,
cloud reaction generation, UI counters, OmniVoice wiring, and owned-process
cleanup exist. However, no real screen-aware reaction had reached audio before
work paused. The final 640×360 vision payload plus Ling-to-OpenRouter fallback
was committed and pushed but has not been live-tested.

Do not report the feature complete until `đã phát` increases and the user
confirms that the audible voice is the existing Ru OmniVoice profile.

## Product contract that must not change

- Observe only the Windows primary monitor.
- Observation is manual and off on every app launch; never auto-enable it.
- Capture is continuous from the user's perspective, but representative frames
  stay in a bounded RAM-only ring. Never write frames, screenshots, video,
  OCR, observations, prompts, or reactions to disk.
- Raw pixels may go only to the owned loopback Qwen server. Kilo/OpenCode may
  receive the local VLM's unfiltered textual observation, never image bytes.
- Do not add sensitive-content filtering, redaction, privacy pauses, or other
  policy machinery. The user explicitly rejected it for this local prototype.
- Every final screen-aware reaction is generated dynamically. Do not add canned
  lines or prepared catchphrases.
- Ru is a social companion for conversation and playful banter, not a screen
  coding assistant, debugger, gameplay coach, or computer-control agent.
- Every accepted reaction must use exactly `Ru (OmniVoice)`. Never substitute a
  browser/system voice and never emit a text-only screen reaction.
- Ru may talk over game/video audio. System-audio capture, transcription,
  analysis, and ducking are not part of this milestone.
- Preserve private VTS metadata boundaries and the existing `VoiceVolume`
  lip-sync path. Never expose VTS/voice markers in TTS, chat, history, or memory.
- Keep natural Vietnamese `anh`/`em`, no emojis or ASCII emoticons, and no
  fabricated capabilities.
- Target roughly three to six distinct comments per active minute and a
  preferred event-to-first-audio latency around 2–3 seconds. Do not make late
  speech appear successful by silently raising the stale deadline.

## Implemented dataflow

```text
Primary monitor
  -> DXGI capture every 500 ms
  -> 20-frame / 10-second RAM ring + fingerprint change gate
  -> newest 640x360 JPEG only
  -> local Qwen3-VL llama-server on 127.0.0.1:18111
  -> structured text observation
  -> authenticated FastAPI /observation/react on 127.0.0.1:8000
  -> Kilo/OpenCode short Ru reaction
  -> Tauri ru://screen-reaction event
  -> exact Ru OmniVoice on 127.0.0.1:5052
  -> optional private VTube Studio catalog action
```

All app-owned descendants are assigned to the existing Windows kill-on-close
Job Object. Closing the Tauri parent has repeatedly removed backend, voice, and
vision listeners.

## Final implementation state

### Capture and local vision

- `SAMPLE_INTERVAL = 500 ms`
- `RING_CAPACITY = 20`, `MAX_FRAME_AGE = 10 s`
- `ANALYSIS_BATCH_SIZE = 1` (only the newest frame enters Qwen)
- prepared image maximum: `480×270`, JPEG quality `80`
- llama-server: loopback only, one slot, Qwen3-VL-2B GGUF + pinned mmproj
- context: `2048`
- image token budget: `96`
- JSON completion budget: `512` (keep this unless parse reliability is
  re-proven; 192 tokens previously truncated most real outputs)
- local reasoning: off
- strict Rust parser still enforces exact fields, finite scores, score ranges,
  event length, and visible-text bounds even where llama.cpp b10142 cannot
  compile those JSON-Schema limits.

### Cloud reaction route

- Normal chat model selection is unchanged.
- Only when chat uses Kilo `kilo-auto/free`, screen reactions use:
  - primary: `inclusionai/ling-3.0-flash:free`
  - output budget: `256`
  - `reasoning.effort = none`
  - fallback: `openrouter/free`, but only after immediate HTTP
    429/502/503/504 from Ling
- Do not chain fallback after a slow request or total deadline; that would
  produce stale speech.
- Python hosted streaming now enforces a real total deadline even if reasoning
  chunks continue arriving. The screen request deadline is 4 seconds; the Rust
  bridge request timeout remains 10 seconds as an outer guard.
- The standalone screen prompt is intentionally much shorter than the full chat
  prompt and still requires one short Vietnamese Ru line plus valid optional
  private VTS metadata.

### Staleness and diagnostics

- `MAX_EVENT_AGE_MS = 4500`
- three cloud failures pause observation and release vision resources
- the UI displays only RAM-only numeric diagnostics:
  - frame / vision / candidate counts
  - confidence/novelty/worthiness rejection counts and last scores
  - cloud attempts/skips/failures
  - pre-cloud age / cloud duration / post-cloud age
  - stale drops and emitted reactions
- It must never display or persist the observation or generated reaction text.

## What was diagnosed and fixed

1. Tauri command errors were swallowed by the React UI. The actual error text
   is now shown and the error state can be retried.
2. OmniVoice cold startup exceeded the generic bridge timeout. Preflight now
   has a separate 90-second timeout; normal requests remain bounded.
3. `fastapi.Request` shadowed `urllib.request.Request`, causing
   `Request.__init__() got an unexpected keyword argument 'data'`. Imports are
   now explicitly aliased.
4. Qwen's 192-token production response budget yielded roughly one parseable
   result in 23 analyses. Production now keeps 512 output tokens.
5. Qwen often reports high confidence but subjective novelty/worthiness as
   zero. Those subjective scores remain measured but no longer veto every
   confident candidate.
6. Kilo `kilo-auto/free` spent 64 and 256 output tokens entirely on hidden
   reasoning and returned no spoken content. Screen reactions therefore have a
   separate direct model route and reasoning configuration.
7. Poolside Laguna S passed early probes but later stalled until the ten-second
   Rust timeout, so it is no longer the primary screen route.
8. With OmniVoice resident, the original vision config (4096 context, 512 image
   tokens) spilled memory and took 4.61–5.20 seconds per synthetic request.
   Reducing to 2048/256 produced a valid co-resident three-frame synthetic parse
   in 917 ms.
 9. Real 960×540 frames were still too slow, so production now sends only the
    newest 640×360 frame. This final resolution has not been live-measured.
10. Added counters separated candidate rejection, cloud skips/failures, stale
    drops, and playback emission. This proved that the earlier silence happened
    before TTS; `ru://screen-reaction` was never emitted.
11. Live-measured at 640×360: `ms trước/cloud/sau 3937/4345/7832` and `đã phát 0`
    because every reply crossed the 4500 ms stale gate. Reducing to 480×270 cut
    pre-cloud to ~2.6–2.9 s.
12. Coordinator acknowledgement bug: FastAPI called `complete()` at
    `/observation/react` time, before Rust knew whether the reply was dropped
    stale or actually spoken. Stale replies still armed the 10 s cooldown and
    populated recent reactions, inflating `cloudSkips` to 70% (35/50). Fixed with
    a dedicated authenticated `/observation/ack` endpoint: the coordinator
    commits only after Tauri reports a real emitted/played outcome (with a 15 s
    pending expiry fallback). Skips fell to 12–22%.
13. At 480×270/128 tokens, pre-cloud was still 3.5–3.9 s under OmniVoice
    residency, so post-cloud again crossed the gate (`stale 21/26`, `đã phát 0`).
    Reducing image tokens to 96 brought pre-cloud to ~2.5–2.7 s and post-cloud
    to ~3.2–4.1 s, letting accepted reactions through.
14. Screen-reaction text generation uses the cloud (Kilo) route, not Qwen. Kilo
    intermittently returns an empty SSE stream; each failure is one `Provider`
    skip-free failure and three consecutive failures pause observation as
    designed. Backend stderr now flows to the Tauri dev console and the Rust
    bridge logs the failure kind so this remains diagnosable without persisting
    prompts, keys, or screen content.

## Important live evidence

- Original stalled run:
  `cloud a/s/f 20/13/0`, `stale 18`, `đã phát 0`.
- After reducing llama context/image tokens:
  stale fell substantially, but a valid response still crossed the deadline.
- Poolside live failure:
  `ms trước/cloud/sau 2842/10012/12854`, then provider failure.
- Ling live completion before the final resolution change:
  `ms trước/cloud/sau 3391/2289/5680`; it was correctly dropped as stale.
- Direct Ling probe with 256 tokens:
  first spoken content at 1.621 s, complete at 1.674 s, natural `anh`/`em`.
- Direct `openrouter/free` fallback probe:
  complete at 1.066 s with correct `anh`/`em`.
- **Audible success, 640×360 baseline**: `đã phát 1` with cloud
  `a/s/f 22/11/6`; user confirmed the audible voice is Ru's existing OmniVoice.
- **Audible success, 480×270/96 tokens**: `đã phát 6` accumulated; last line
  `ms trước/cloud/sau 2589/1236/3825` (under the 4500 ms gate);
  `cloud a/s/f 42/11/15` — the failures are Kilo empty-streams, not local.
- Event-to-first-audio: 6 samples, p50 6549 ms, p95 8092 ms, worst 8092 ms.
- Live cloud-loss pause observed: three consecutive `Provider` failures
  transitioned the UI to "Observation tạm dừng" and released the vision server.

## Verification status (2026-08-02)

Live-verified end to end with `npm run tauri dev`:

- `cloud attempts > skips + failures` and `post-cloud age < 4500 ms` — yes at
  480×270/96 tokens (post-cloud 3239–4678 ms across runs; typical 3.2–4.1 s).
- `đã phát > 0` and user confirmed the audible voice is Ru's OmniVoice.
- Coordinator ack semantics fixed and covered by Python + Rust tests
  (pending expiry, fail-without-cooldown, played-commit, unknown-id harmless).
- Complete verification green: 286 pytest, 11 vitest + production build,
  32 cargo (1 interactive DXGI ignored), fmt/clippy clean.
- 20 observation on/off cycles: exactly one llama-server per active state, no
  process duplication, total RAM oscillated 7.3–8.3 GB with no upward trend.
- 10 forced parent kills: zero leftover processes and zero listeners on
  8000/5052/18111 after each kill.
- Single-instance: a second launch is refused at the Vite port and never
  duplicates backend/voice/vision processes.
- Cloud-loss pause: three consecutive provider failures pause observation and
  release the vision server, exactly as designed.
- No image/network leakage and no screen-content persistence: unchanged
  (RAM-only pipeline; UI and logs show numeric diagnostics only).

### Remaining observations (not code defects)

- Cloud (Kilo) intermittently returns empty SSE streams; during degraded
  windows ~40% of attempts failed and fewer reactions flowed. Each failure
  counts once and does not arm the cooldown; three consecutive failures pause
  as specified. Revisit provider stability (handoff step 3) only with user
  approval to change providers or spend credits.
- Representative quality trial across game/YouTube/subtitle/static/
  scene-transition scenes, and a dedicated ten-minute cadence/repetition
  review with a known screen, remain manual listening items.

## Verification commands

From `D:\LLM\gui-tauri`:

```powershell
npm run tauri dev
```

Do not launch `target\debug\gui-tauri.exe` directly in development. It expects
the Vite server and otherwise opens a WebView error page saying localhost
refused the connection.

In the app, click **Bắt đầu quan sát**, show a moving game/video screen, and
copy the complete numeric line. Required first success:

```text
cloud attempts > skips + failures
post-cloud age < 4500 ms
stale does not increase for that accepted request
đã phát > 0
```

Then ask the user to confirm that the audible voice is Ru's existing
OmniVoice, not merely that audio played.

### 2. If pre-cloud age is still too high

Change one variable at a time and keep the 512-token JSON output budget unless
parse reliability is rerun:

1. measure real `ms trước/cloud/sau` first;
2. try 480×270 prepared frames;
3. only then consider image tokens 128;
4. confirm useful observations on game/video/subtitle/static scenes;
5. do not raise the 4500 ms stale deadline just to increase `đã phát`.

Benchmark while OmniVoice remains resident. Running Qwen alone hides the VRAM
contention that caused the real regression.

### 3. If cloud failures continue

- Preserve Ling as a preference only while evidence supports it; the user said
  it may be changed if unstable.
- Inspect only provider status/type/timing, never API keys, prompts, screen
  content, or generated text in persistent logs.
- Confirm whether the Ling-to-`openrouter/free` fallback executes on immediate
  429/502/503/504 and completes inside the stale budget.
- Probe candidate models with the exact short screen prompt and at least three
  runs. Reject models that are fast but use `mày`/`bạn`, invent VTS codes, emit
  only reasoning, or have unstable tail latency.
- A paid/BYOK fast model can later be added, but do not assume authorization to
  spend credits or add a new provider key.

### 4. Fix coordinator acknowledgement semantics if stale replies still cause skips

Current FastAPI flow calls `ReactionCoordinator.complete(...)` before Rust
knows whether the reply was emitted or spoken. When Rust later discards the
reply as stale, FastAPI still records the line and starts cooldown/repeat
windows. This produced one stale success followed by many `skipped` requests.

Preferred fix: add an authenticated acknowledgement/failure endpoint so the
coordinator commits cooldown/recent-reaction state only after Tauri reports a
real emitted/played outcome. Cover stale, playback failure, cancellation, chat
interruption, and app shutdown with tests. Do not solve it by clearing all
cooldowns or allowing repetitive bursts.

### 5. If `đã phát` increases but nothing is heard

- Read the temporary playback status in `ObservationControl`.
- Verify `/voice/tts` returns a complete WAV for the supplied reaction.
- Verify `speakText` calls `onPlaying`, then reports
  `observation_voice_started` and `observation_voice_finished`.
- Confirm the forced label is exactly `Ru (OmniVoice)` and the current Ru
  reference assets are used.
- Never fall back to browser/system TTS.

### 6. Run complete verification after the first audible success

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe -m pytest -q

cd D:\LLM\gui-tauri
npm test -- --run
npm run build

cd D:\LLM\gui-tauri\src-tauri
cargo fmt --check
cargo test --locked
cargo check --locked
```

Status 2026-08-02 after the ack and token-budget edits: 286 pytest,
11 vitest + production build, 32 cargo (1 interactive DXGI ignored) — all green.

## Key files

- `gui-tauri/src-tauri/src/frame_buffer.rs` — in-memory resize/JPEG/ring/gate
- `gui-tauri/src-tauri/src/screen_capture.rs` — primary-monitor DXGI capture
- `gui-tauri/src-tauri/src/vision_runtime.rs` — llama-server config and typed VLM request
- `gui-tauri/src-tauri/src/observation_runtime.rs` — workers, stale gate, metrics, events
- `gui-tauri/src-tauri/src/backend_bridge.rs` — authenticated loopback reaction bridge
- `gui-tauri/src/components/ObservationControl.tsx` — toggle, counters, mandatory voice playback
- `gui-tauri/src/lib/observationClient.ts` — typed observation command/event boundary
- `src/mini_gpt/screen_reaction.py` — cadence, repeat, stale, and recent-reaction coordinator
- `src/mini_gpt/headless_runner.py` — short prompt route, Ling primary, conditional fallback
- `src/mini_gpt/openai_runtime.py` — SSE parsing, model/reasoning overrides, total deadline
- `src/mini_gpt/server.py` — preflight/react/cancel APIs and exact voice selection
- `scripts/benchmark_local_vision.py` — in-memory local vision benchmark
- `docs/superpowers/specs/2026-08-01-ru-local-screen-awareness-design.md` — approved design
- `docs/superpowers/plans/2026-08-02-ru-local-screen-awareness.md` — implementation record

## Safe shutdown and process verification

Stop `npm run tauri dev` with Ctrl+C in its own terminal. Then verify:

```powershell
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
  Where-Object { $_.LocalPort -in 1420, 8000, 5052, 18111 } |
  Select-Object LocalPort, OwningProcess
```

Expected after shutdown: no rows. Do not kill unrelated Python, WebView, browser,
or llama processes by name; resolve the exact app-owned PID/path first.

## Documentation rule

After implementation or verification, update this file, `TODO.md`,
`docs/ANTIGRAVITY_HANDOFF.md`, the design status, and README behavior if the
actual route/config changes. Remove statements that claim screen awareness is
unimplemented. Record failed live paths honestly instead of rewriting them as
success.
