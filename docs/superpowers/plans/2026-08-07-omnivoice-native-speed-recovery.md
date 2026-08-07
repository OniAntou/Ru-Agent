# OmniVoice Native Speed Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove metallic local DSP effects while preserving bounded, opt-in
voice pacing through OmniVoice's native generation speed.

**Architecture:** Keep the existing local FastAPI and Flask loopback boundary.
Retire all non-original voice styles at validation time, resolve historical
saved values to `original`, and let routes calculate an effective speed only
when automatic pacing is enabled. The loopback passes a non-default effective
speed directly to `model.generate`; it writes the generated waveform without a
post-processor.

**Tech Stack:** Python 3.13, FastAPI/Pydantic, Flask, OmniVoice 0.2.1, pytest,
WinUI 3/.NET 10, xUnit.

## Global Constraints

- Work directly on approved `main`; preserve user-owned `build.bat`.
- Keep `share_memory_with_cloud` true by default.
- Do not download models, add dependencies, or run GPU-heavy checks in
  parallel.
- Keep `VOICE_PACE` and VTube metadata private from chat, audio text, history,
  memory, and logs.
- The quality baseline is clone-only 1.00x audio; native non-1.00x speed needs
  a fresh Ru listening check before quality acceptance.

---

### Task 1: Retire the DSP style contract

**Files:**
- Modify: `tests/test_voice_style.py`
- Modify: `tests/test_server_api.py`
- Modify: `tests/test_voice_stream.py`
- Modify: `tests/test_omnivoice_tts_server.py`
- Modify: `src/mini_gpt/voice_style.py`

**Interfaces:**
- Produces `VoiceStyleId = Literal["original"]` and
  `voice_style_from_saved(value) -> VoiceStyle` that resolves historical style
  IDs to `original`.
- Consumes the existing `voice_style_by_id` validation boundary in settings,
  stream, and loopback routes.

- [x] **Step 1: Write failing style-regression tests.**

```python
@pytest.mark.parametrize("retired", ["low_pitch", "high_pitch", "whisper"])
def test_saved_retired_style_resolves_to_original(retired: str) -> None:
    assert voice_style_from_saved(retired).id == ORIGINAL_STYLE_ID

@pytest.mark.parametrize("retired", ["low_pitch", "high_pitch", "whisper"])
def test_direct_retired_style_is_rejected(retired: str) -> None:
    with pytest.raises(ValueError, match="Unsupported OmniVoice voice style"):
        voice_style_by_id(retired)
```

- [x] **Step 2: Run the focused style tests and verify RED.**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_voice_style.py -q`

Expected: direct retired-style assertions fail because `low_pitch` and
`high_pitch` are still accepted.

- [x] **Step 3: Implement the minimal one-style mapping.**

```python
VoiceStyleId: TypeAlias = Literal["original"]
VOICE_STYLES = (VoiceStyle("original", "Giữ giọng gốc"),)
```

Retain the forgiving saved-value resolver but make direct validation reject all
retired IDs. Remove tests that expect numeric pitch offsets or forwarding a
non-original style.

- [x] **Step 4: Run the focused style, transport, and loopback tests.**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_voice_style.py tests/test_server_api.py tests/test_voice_stream.py tests/test_omnivoice_tts_server.py -q`

Expected: all selected tests pass after their retired-style expectations are
updated.

### Task 2: Replace local waveform effects with native OmniVoice speed

**Files:**
- Modify: `tests/test_omnivoice_tts_server.py`
- Modify: `src/mini_gpt/omnivoice_tts_server.py`

**Interfaces:**
- `synthesize_fast(text, profile, speed, voice_style_id, timing)` validates the
  style, then supplies `speed` to `model.generate` only when it differs from
  `DEFAULT_VOICE_SPEED`.
- It returns a WAV path with the generated mono waveform and exposes no
  `apply_voice_speed` or `apply_voice_pitch` production helper.

- [x] **Step 1: Write the failing native-speed generation test.**

```python
fast_audio = server.synthesize_fast("Chào anh.", "ru", speed=1.08)
baseline_audio = server.synthesize_fast("Chào anh.", "ru", speed=1.0)

assert model.calls[0]["speed"] == 1.08
assert "speed" not in model.calls[1]
assert Path(fast_audio).is_file()
assert Path(baseline_audio).is_file()
```

- [x] **Step 2: Run it and verify RED.**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_omnivoice_tts_server.py -q`

Expected: the non-default model call lacks `speed`; the old tests still refer
to the now-unwanted DSP helpers.

- [x] **Step 3: Implement direct native generation.**

```python
speed = clamp_voice_speed(speed)
generate_kwargs = {"text": text, "voice_clone_prompt": prompt, **profile_kwargs}
if speed != DEFAULT_VOICE_SPEED:
    generate_kwargs["speed"] = speed
audio = get_model().generate(**generate_kwargs)
waveform = _as_waveform(audio[0])
```

Delete the `librosa` state plus the local pitch/speed helper functions and
remove the obsolete `pitch_ms` milestone from timing tests.

- [x] **Step 4: Run the loopback test module and verify GREEN.**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_omnivoice_tts_server.py -q`

Expected: all loopback unit tests pass without importing librosa.

### Task 3: Honor opt-in automatic pace in every route

**Files:**
- Modify: `tests/test_server_api.py`
- Modify: `src/mini_gpt/routers/voice.py`
- Modify: `src/mini_gpt/routers/observation.py`
- Modify: `src/mini_gpt/_server_shared.py`

**Interfaces:**
- All three routes call `effective_voice_speed(...,
  auto_pace_enabled=settings["auto_pace_enabled"], ...)`.
- Fresh settings default `auto_pace_enabled` to `False`.

- [x] **Step 1: Write failing route regression tests.**

```python
client.post("/settings", json={"auto_pace_enabled": False})
client.post("/voice/tts", json={"text": "Chào anh. [[VOICE_PACE:quick]]"})
assert sent == [("Chào anh.", "Ru (OmniVoice)", 1.0, "original")]
```

Add equivalent stream and observation coverage using the existing fake voice
request/coordinator hooks; the `true` case must still send 1.08.

- [x] **Step 2: Run only these route tests and verify RED.**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py -q`

Expected: disabled quick pace currently sends 1.08 because all routes
hard-code `True`.

- [x] **Step 3: Thread the saved boolean into each route.**

```python
auto_pace_enabled = settings.get(
    "auto_pace_enabled", _DEFAULT_SETTINGS["auto_pace_enabled"]
)
speed = effective_voice_speed(base_speed, auto_pace_enabled=auto_pace_enabled, pace=pace)
```

Set the backend default to `False` and use that same default in the voice
status response instead of a literal `True`.

- [x] **Step 4: Run route regressions and verify GREEN.**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py tests/test_voice_stream.py -q`

Expected: disabled pace retains the manual speed; enabled pace retains the
bounded native-speed request.

### Task 4: Remove the rejected UI control and align managed defaults

**Files:**
- Modify: `gui-winui3/Views/VoiceView.xaml`
- Modify: `gui-winui3/Views/VoiceView.xaml.cs`
- Modify: `gui-winui3/Models/Models.cs`
- Modify: `gui-winui3.Tests/SettingsDtoTests.cs`

**Interfaces:**
- `VoiceView.SaveVoiceSettingsAsync()` sends the compatibility value
  `"original"` without a style selector.
- `AutoPaceEnabled` defaults to false in all C# setting DTOs.

- [x] **Step 1: Write the failing DTO default test.**

```csharp
[Fact]
public void VoiceSettingsDefaultToOriginalWithoutAutomaticPace()
{
    var settings = new VoiceSettingsUpdateDto();
    Assert.Equal("original", settings.VoiceStyleId);
    Assert.False(settings.AutoPaceEnabled);
}
```

- [x] **Step 2: Run the xUnit suite and verify RED.**

Run: `dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug --no-restore`

Expected: the default assertion fails because the DTO currently defaults to
`true`.

- [x] **Step 3: Implement the minimal UI/default change.**

Remove `VoiceStyleComboBox` and `OnVoiceStyleChanged`, pass `"original"` from
`SaveVoiceSettingsAsync`, make the toggle default off in XAML, and set every
voice DTO's `AutoPaceEnabled` initializer to `false`.

- [x] **Step 4: Run xUnit and build the GUI.**

Run: `dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug --no-restore`

Run: `cmd /c build.bat`

Expected: tests pass and the self-contained Release executable is published
without changing `build.bat`.

### Task 5: Verify the live source and documentation

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-08-07-omnivoice-native-speed-recovery-design.md`
- Modify: `docs/superpowers/plans/2026-08-07-omnivoice-native-speed-recovery.md`

- [x] **Step 1: Run the complete automated suite sequentially.**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest -q`

Run: `dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug --no-restore`

Run: `git diff --check`

Result: Python suite 440 passed; WinUI xUnit 50 passed; `git diff --check`
reported no whitespace errors. Only the unrelated `build.bat` stays unstaged.

- [x] **Step 2: Restart only the verified OmniVoice loopback service and run a sequential Ru check.**

Evidence captured 2026-08-08 on the rebuilt Release branch with the verified
local OmniVoice loopback service restarted on port 5052 (singleton guard
`Local\RuOmniVoice5052`, warmup 24.3 s). A fixed non-sensitive Vietnamese
sentence was requested at the three native speeds in sequence; each response
was a 200 24 kHz mono 16-bit WAV.

| Requested | Actual WAV | X-Synthesis-Ms | Response |
|-----------|-----------|----------------|----------|
| 1.00x | 78,720 frames (3.28 s) | 1,193 ms | `speed` omitted (clone baseline) |
| 0.92x | 85,440 frames (3.56 s) | 1,189 ms | `speed=0.92` passed to `model.generate` |
| 1.08x | 72,960 frames (3.04 s) | 1,439 ms | `speed=1.08` passed to `model.generate` |

Duration scales as `1/speed`: 1.00x=3.28 s, 0.92x=3.56 s, 1.08x=3.04 s,
confirming the loopback uses OmniVoice's native `speed` argument and no local
pitch/time-stretch processor. Comparison WAVs were written only under the
pre-approved temp probe directory and were not added to the repository.
Native non-1.00x quality remains pending the user's listening acceptance.

- [x] **Step 3: Update project documentation with actual evidence.**

Document that pitch DSP is removed, auto pace defaults off and is respected,
and native non-1.00x quality remains pending the user's listening acceptance.
Keep `share_memory_with_cloud=true` documented as unchanged. Mark the prior
pitch experiment as superseded/rejected rather than deleting its history.
