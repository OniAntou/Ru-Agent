# OmniVoice Voice-Style Presets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Add persisted, quality-safe OmniVoice voice-style presets to Ru without changing the default clone voice or leaking voice metadata into conversation content.

**Architecture:** A shared Python voice-style contract validates one stable ID and maps it to an optional OmniVoice instruct string. FastAPI resolves the saved ID once per TTS request, carries it through the existing HTTP/WebSocket coordinator, and the loopback service injects it only at model.generate. WinUI exposes the same IDs in a ComboBox and saves them through the existing settings endpoint.

**Tech Stack:** Python 3.13, FastAPI, Pydantic v2, Flask, OmniVoice, pytest, C#/.NET 10, WinUI 3, xUnit.

## Global Constraints

- Preserve the user-owned build.bat change; never modify or stage it.
- Keep share_memory_with_cloud defaulting to true.
- original is the persisted default and must omit OmniVoice instruct entirely.
- Allow only original, low_pitch, high_pitch, and whisper; do not accept raw user prompt text.
- Keep style IDs and instructions out of chat, TTS input text, conversation history, memory, self-learning, VTube metadata, and user-content logs.
- Preserve vi-normalized-16, the 1.00x quality baseline, bounded pace behavior, and the single-GPU lifecycle.
- Run Python, WinUI, and OmniVoice checks sequentially; the RTX 4050 has 6,141 MiB VRAM.
- Update documents only with observed implementation and verification facts; delete no unrelated document.

---

## File map

- src/mini_gpt/voice_style.py: immutable allowed IDs, Vietnamese labels, optional instructions, strict lookup, and safe saved-value migration.
- src/mini_gpt/_server_shared.py: default/normalization and loopback TTS JSON payload.
- src/mini_gpt/routers/settings.py: strict settings request validation and persistence.
- src/mini_gpt/routers/voice.py and src/mini_gpt/voice_stream.py: resolve and carry the ID through direct and streamed speech.
- src/mini_gpt/omnivoice_tts_server.py: map the ID to the optional instruct keyword immediately before generation.
- gui-winui3/Models/Models.cs, Services/ApiService.cs, Views/VoiceView.xaml, and Views/VoiceView.xaml.cs: typed settings transport and selector.
- tests/test_voice_style.py, tests/test_server_api.py, tests/test_voice_stream.py, tests/test_omnivoice_tts_server.py, and gui-winui3.Tests/SettingsDtoTests.cs: regression coverage.
- README.md and docs/superpowers/specs/2026-08-07-omnivoice-voice-style-presets-design.md: user guidance and actual verification status.

## Task 1: Create the style contract and persisted setting

**Files:**

- Create: src/mini_gpt/voice_style.py
- Create: tests/test_voice_style.py
- Modify: src/mini_gpt/_server_shared.py:111-129 and 258-349
- Modify: src/mini_gpt/routers/settings.py:1-147
- Modify: src/mini_gpt/routers/voice.py:133-169
- Modify: tests/test_server_api.py:178-198 and 409-478

**Interfaces:**

| Symbol | Contract |
| --- | --- |
| VoiceStyleId | Literal string union: original, low_pitch, high_pitch, whisper. |
| VoiceStyle | Frozen dataclass with id, Vietnamese UI label, and optional OmniVoice instruction. |
| ORIGINAL_STYLE_ID | The literal value original. |
| voice_style_by_id(value: str) | Returns the matching VoiceStyle or raises ValueError with Unsupported OmniVoice voice style. |
| voice_style_from_saved(value: object) | Returns the matching VoiceStyle or original for a missing/malformed historical value. |

The strict lookup raises ValueError for unknown values. The saved-value lookup returns original for a missing, malformed, or unknown historical value.

- [x] **Step 1: Write the failing contract and settings tests.**

~~~python
# tests/test_voice_style.py
import pytest
from mini_gpt.voice_style import (
    ORIGINAL_STYLE_ID,
    voice_style_by_id,
    voice_style_from_saved,
)

@pytest.mark.parametrize(
    ("style_id", "instruct"),
    [
        ("original", None),
        ("low_pitch", "low pitch"),
        ("high_pitch", "high pitch"),
        ("whisper", "whisper"),
    ],
)
def test_known_voice_styles_are_stable_and_explicit(style_id, instruct):
    style = voice_style_by_id(style_id)
    assert style.id == style_id
    assert style.instruct == instruct

def test_saved_missing_or_unknown_style_falls_back_to_original():
    assert voice_style_from_saved(None).id == ORIGINAL_STYLE_ID
    assert voice_style_from_saved("invalid").id == ORIGINAL_STYLE_ID
    assert voice_style_from_saved(["invalid"]).id == ORIGINAL_STYLE_ID

def test_direct_unknown_style_is_rejected():
    with pytest.raises(ValueError, match="Unsupported OmniVoice voice style"):
        voice_style_by_id("custom prose")
~~~

~~~python
# tests/test_server_api.py
def test_settings_voice_style_round_trip_and_status(monkeypatch, isolated_state):
    monkeypatch.setattr(server.core.runner, "load", lambda *args, **kwargs: "Ready")
    with TestClient(server.app) as client:
        saved = client.post("/settings", json={"voice_style_id": "low_pitch"})
        settings = client.get("/settings").json()
        status = client.get("/voice/status").json()
    assert saved.json()["status"] == "success"
    assert settings["voice_style_id"] == "low_pitch"
    assert status["voice_style_id"] == "low_pitch"

def test_settings_reject_unknown_voice_style(isolated_state):
    with TestClient(server.app) as client:
        response = client.post("/settings", json={"voice_style_id": "free-form"})
    assert response.status_code == 422
~~~

- [x] **Step 2: Run the focused tests and confirm they fail.**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_voice_style.py tests\test_server_api.py -q
~~~

Expected: failure importing mini_gpt.voice_style and absent voice_style_id response fields.

- [x] **Step 3: Implement the contract and migration.**

~~~python
# src/mini_gpt/voice_style.py
VOICE_STYLES = (
    VoiceStyle("original", "Giữ giọng gốc", None),
    VoiceStyle("low_pitch", "Trầm hơn", "low pitch"),
    VoiceStyle("high_pitch", "Cao hơn", "high pitch"),
    VoiceStyle("whisper", "Thì thầm", "whisper"),
)
_VOICE_STYLES_BY_ID = {style.id: style for style in VOICE_STYLES}

def voice_style_by_id(value: str) -> VoiceStyle:
    if not isinstance(value, str):
        raise ValueError(f"Unsupported OmniVoice voice style: {value!r}")
    style = _VOICE_STYLES_BY_ID.get(value)
    if style is None:
        raise ValueError(f"Unsupported OmniVoice voice style: {value!r}")
    return style

def voice_style_from_saved(value: object) -> VoiceStyle:
    if not isinstance(value, str):
        return _VOICE_STYLES_BY_ID[ORIGINAL_STYLE_ID]
    return _VOICE_STYLES_BY_ID.get(value, _VOICE_STYLES_BY_ID[ORIGINAL_STYLE_ID])
~~~

Add voice_style_id: original to _DEFAULT_SETTINGS. In both existing effective-settings functions, normalize with voice_style_from_saved and store the resulting ID. Add a VoiceStyleId | None Pydantic field, save it only when present, include it in the existing voice-only update predicate, and return it from voice status. Do not change the cloud-memory default.

- [x] **Step 4: Run the focused contract/settings tests.**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_voice_style.py tests\test_server_api.py -q
~~~

Expected: all tests pass; old settings without voice_style_id read as original and a new invalid request receives HTTP 422.

- [x] **Step 5: Commit the style contract and persistence layer.**

~~~powershell
git add src/mini_gpt/voice_style.py src/mini_gpt/_server_shared.py src/mini_gpt/routers/settings.py src/mini_gpt/routers/voice.py tests/test_voice_style.py tests/test_server_api.py
git commit -m "feat: persist OmniVoice voice style presets"
~~~

## Task 2: Transport the style ID and inject it only in OmniVoice

**Files:**

- Modify: src/mini_gpt/_server_shared.py:579-595
- Modify: src/mini_gpt/routers/voice.py:95-221
- Modify: src/mini_gpt/voice_stream.py:13-138
- Modify: src/mini_gpt/omnivoice_tts_server.py:178-322
- Modify: tests/test_server_api.py:570-760
- Modify: tests/test_voice_stream.py:19-116
- Modify: tests/test_omnivoice_tts_server.py:90-260

**Interfaces:**

| Symbol | Contract |
| --- | --- |
| request_voice_audio | Accepts text, voice label, speed, and VoiceStyleId; posts only the normalized style ID to loopback TTS and returns WAV bytes. |
| VoiceStreamCoordinator.start | Accepts the same VoiceStyleId once per request and forwards it unchanged to every sentence chunk. |
| synthesize_fast | Adds keyword-only voice_style_id defaulting to original and returns a WAV path. |

- [x] **Step 1: Write failing transport and generation tests.**

~~~python
# tests/test_omnivoice_tts_server.py
def capture_generate_kwargs(tmp_path, monkeypatch):
    import numpy as np
    captured = {}

    class FakeInferenceMode:
        def __enter__(self):
            return self
        def __exit__(self, exc_type, exc_value, traceback):
            return None

    class FakeTorch:
        @staticmethod
        def inference_mode():
            return FakeInferenceMode()

    class FakeModel:
        def generate(self, **kwargs):
            captured.update(kwargs)
            return [np.zeros((1, 2400), dtype=np.float32)]

    class FakeSoundFile:
        @staticmethod
        def write(path, data, samplerate):
            Path(path).write_bytes(b"RIFF")

    monkeypatch.setitem(sys.modules, "torch", FakeTorch())
    monkeypatch.setitem(sys.modules, "soundfile", FakeSoundFile())
    monkeypatch.setattr(server, "warm_model", lambda profile: {"ready": True})
    monkeypatch.setattr(server, "get_model", lambda: FakeModel())
    monkeypatch.setattr(server, "apply_voice_speed", lambda audio, speed: audio)
    monkeypatch.setattr(
        server.tempfile,
        "NamedTemporaryFile",
        lambda **kwargs: type(
            "Output",
            (),
            {"name": str(tmp_path / "speech.wav"), "close": lambda self: None},
        )(),
    )
    server._voice_clone_prompts.clear()
    server._voice_clone_prompts["ru"] = "ru-prompt"
    return captured

def test_synthesis_default_omits_instruct(tmp_path, monkeypatch):
    captured = capture_generate_kwargs(tmp_path, monkeypatch)
    path = server.synthesize_fast("Chào anh.", "ru", voice_style_id="original")
    assert captured["voice_clone_prompt"] == "ru-prompt"
    assert "instruct" not in captured
    Path(path).unlink()

def test_synthesis_passes_validated_preset_instruction(tmp_path, monkeypatch):
    captured = capture_generate_kwargs(tmp_path, monkeypatch)
    path = server.synthesize_fast("Chào anh.", "ru", voice_style_id="low_pitch")
    assert captured["instruct"] == "low pitch"
    Path(path).unlink()

def test_loopback_tts_rejects_unknown_style():
    response = server.create_app().test_client().post(
        "/tts", json={"text": "Chào anh.", "voice_style_id": "free-form"}
    )
    assert response.status_code == 400
~~~

~~~python
# tests/test_voice_stream.py
def test_voice_stream_forwards_one_style_id_to_every_chunk():
    received = []
    coordinator = VoiceStreamCoordinator(
        prepare_speech=lambda selection: None,
        request_audio=lambda text, label, speed, style: received.append(style) or text.encode(),
        memory_lock=threading.Lock(),
    )
    collect_events(
        coordinator, request_id="style-1", text="A. B.",
        voice_label="Ru (OmniVoice)", speed=1.0, voice_style_id="whisper",
    )
    assert received == ["whisper", "whisper"]
~~~

~~~python
# tests/test_server_api.py
def test_voice_tts_uses_saved_style_without_leaking_it_into_text(monkeypatch, isolated_state):
    sent = []
    monkeypatch.setattr(
        voice_router, "request_voice_audio",
        lambda text, label, speed, style: sent.append((text, style)) or b"RIFF",
    )
    with TestClient(server.app) as client:
        client.post("/settings", json={"voice_style_id": "high_pitch"})
        response = client.post(
            "/voice/tts", json={"text": "Chào anh. [[VOICE_PACE:quick]]"}
        )
    assert response.status_code == 200
    assert sent == [("Chào anh.", "high_pitch")]
~~~

- [x] **Step 2: Run the focused TTS tests and confirm the old three-argument path fails.**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_omnivoice_tts_server.py tests\test_voice_stream.py tests\test_server_api.py -q
~~~

Expected: failure because the coordinator, HTTP helper, and loopback server do not accept or forward voice_style_id.

- [x] **Step 3: Implement the style transport.**

Resolve the saved style exactly once in the direct and WebSocket voice routes. Pass only its ID to request_voice_audio and VoiceStreamCoordinator. Update callback annotations and test fakes to accept four arguments. The loopback JSON body must be:

~~~python
{
    "text": text,
    "voice_profile": selection.profile_id,
    "speed": speed,
    "voice_style_id": style.id,
}
~~~

In synthesize_fast, keep the old generation kwargs and append an instruction only for non-default values:

~~~python
style = voice_style_by_id(voice_style_id)
generate_kwargs = {
    "text": text,
    "voice_clone_prompt": _voice_clone_prompts[profile],
    **settings_for_profile(profile).generate_kwargs(),
}
if style.instruct is not None:
    generate_kwargs["instruct"] = style.instruct
audio = get_model().generate(**generate_kwargs)
~~~

Update create_app's injectable synthesis callback to take four arguments. Both loopback TTS endpoints must first require a string raw value, then use strict lookup, return HTTP 400 for an invalid loopback value, and pass the normalized ID into every synthesis call. Do not add any style field to TTS text, WebSocket control events, timing objects, or logs.

- [x] **Step 4: Run focused transport tests.**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_voice_style.py tests\test_omnivoice_tts_server.py tests\test_voice_stream.py tests\test_server_api.py -q
~~~

Expected: all tests pass, default fake-model calls have no instruct, and every sentence receives the same validated style ID.

- [x] **Step 5: Commit the TTS transport.**

~~~powershell
git add src/mini_gpt/_server_shared.py src/mini_gpt/routers/voice.py src/mini_gpt/voice_stream.py src/mini_gpt/omnivoice_tts_server.py tests/test_server_api.py tests/test_voice_stream.py tests/test_omnivoice_tts_server.py
git commit -m "feat: apply OmniVoice voice style presets"
~~~

## Task 3: Expose the preset in WinUI 3

**Files:**

- Modify: gui-winui3/Models/Models.cs:116-199
- Modify: gui-winui3/Services/ApiService.cs:186-205
- Modify: gui-winui3/Views/VoiceView.xaml:17-36
- Modify: gui-winui3/Views/VoiceView.xaml.cs:27-89
- Modify: gui-winui3.Tests/SettingsDtoTests.cs

**Interfaces:**

~~~csharp
public sealed class VoiceSettingsUpdateDto
{
    [JsonPropertyName("voice_label")] public string VoiceLabel { get; set; } = "Ru (OmniVoice)";
    [JsonPropertyName("voice_speed")] public double VoiceSpeed { get; set; } = 1.0;
    [JsonPropertyName("auto_pace_enabled")] public bool AutoPaceEnabled { get; set; } = true;
    [JsonPropertyName("auto_read_replies")] public bool AutoReadReplies { get; set; } = true;
    [JsonPropertyName("voice_style_id")] public string VoiceStyleId { get; set; } = "original";
}
~~~

AppSettingsDto and VoiceStatusDto gain VoiceStyleId with original as default. ApiService.UpdateVoiceAsync gains a final string voiceStyleId parameter and posts VoiceSettingsUpdateDto.

- [x] **Step 1: Write failing DTO wire-format tests.**

~~~csharp
[Fact]
public void VoiceStyleIdUsesTheBackendWireName()
{
    string payload = JsonSerializer.Serialize(new VoiceSettingsUpdateDto
    {
        VoiceStyleId = "low_pitch",
    });

    using JsonDocument document = JsonDocument.Parse(payload);

    Assert.Equal("low_pitch", document.RootElement
        .GetProperty("voice_style_id").GetString());
}

[Fact]
public void SettingsDtoReadsTheSavedVoiceStyle()
{
    AppSettingsDto? settings = JsonSerializer.Deserialize<AppSettingsDto>(
        "{\"voice_style_id\":\"whisper\"}");

    Assert.NotNull(settings);
    Assert.Equal("whisper", settings!.VoiceStyleId);
}
~~~

- [x] **Step 2: Run the GUI DTO tests and confirm they fail.**

~~~powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug --no-restore
~~~

Expected: compilation failure for VoiceSettingsUpdateDto and VoiceStyleId.

- [x] **Step 3: Implement the DTO and Voice view.**

Add the DTO above. Change UpdateVoiceAsync to post it. Add this XAML directly below the existing profile selector:

~~~xml
<Grid ColumnDefinitions="*,Auto" Margin="0,4,0,0">
    <TextBlock Grid.Column="0" Text="Điều chỉnh giọng" VerticalAlignment="Center" Foreground="#DDDDDD"/>
    <ComboBox x:Name="VoiceStyleComboBox" Grid.Column="1" Width="240" SelectionChanged="OnVoiceStyleChanged">
        <ComboBoxItem Content="Giữ giọng gốc" Tag="original"/>
        <ComboBoxItem Content="Trầm hơn" Tag="low_pitch"/>
        <ComboBoxItem Content="Cao hơn" Tag="high_pitch"/>
        <ComboBoxItem Content="Thì thầm" Tag="whisper"/>
    </ComboBox>
</Grid>
~~~

In LoadVoiceSettingsAsync, select the item whose tag equals settings.VoiceStyleId; if none matches, select original. Add OnVoiceStyleChanged using the existing _isUpdatingUi guard and send the selected tag in SaveVoiceSettingsAsync. Do not add a parallel style state to VoicePlaybackService or VoiceStreamClient: the backend resolves the persisted setting for every speech request.

- [x] **Step 4: Run the GUI tests and compile.**

~~~powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug --no-restore
dotnet build gui-winui3\RuApp.csproj -c Debug -r win-x64 --no-restore
~~~

Expected: all xUnit tests pass and the Voice view compiles with the new selector.

- [x] **Step 5: Commit the GUI selector.**

~~~powershell
git add gui-winui3/Models/Models.cs gui-winui3/Services/ApiService.cs gui-winui3/Views/VoiceView.xaml gui-winui3/Views/VoiceView.xaml.cs gui-winui3.Tests/SettingsDtoTests.cs
git commit -m "feat: add OmniVoice voice style controls"
~~~

## Task 4: Verify the release, listen to Vietnamese samples, and update documents

**Files:**

- Modify: README.md:340-397
- Modify: docs/superpowers/specs/2026-08-07-omnivoice-voice-style-presets-design.md
- Modify: docs/superpowers/plans/2026-08-07-omnivoice-voice-style-presets.md
- Review: TODO.md and changed project documentation

- [x] **Step 1: Run the full automated suite sequentially.**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --configuration Debug --no-restore
git diff --check
~~~

Expected: all tests pass; no whitespace errors; build.bat remains an unstaged user-owned modification.

- [x] **Step 2: Build the Release GUI without modifying the build script.**

~~~powershell
cmd /c build.bat
~~~

Expected: a self-contained executable exists at gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe.

- [ ] **Step 3: Run sequential Vietnamese listening probes.**

With no local Llama process resident, use the already-installed OmniVoice environment and call the real adapter one profile/style at a time:

~~~powershell
& D:\LLM\omnivoice_env\Scripts\python.exe -c "import sys; sys.path.insert(0, r'D:\LLM\src'); from mini_gpt.omnivoice_tts_server import synthesize_fast; print(synthesize_fast('Chào anh, hôm nay em nói tiếng Việt và English thật tự nhiên nhé.', 'ru', 1.0, voice_style_id='original'))"
& D:\LLM\omnivoice_env\Scripts\python.exe -c "import sys; sys.path.insert(0, r'D:\LLM\src'); from mini_gpt.omnivoice_tts_server import synthesize_fast; print(synthesize_fast('Chào anh, hôm nay em nói tiếng Việt và English thật tự nhiên nhé.', 'ru', 1.0, voice_style_id='low_pitch'))"
& D:\LLM\omnivoice_env\Scripts\python.exe -c "import sys; sys.path.insert(0, r'D:\LLM\src'); from mini_gpt.omnivoice_tts_server import synthesize_fast; print(synthesize_fast('Chào anh, hôm nay em nói tiếng Việt và English thật tự nhiên nhé.', 'ai_vy', 1.0, voice_style_id='original'))"
& D:\LLM\omnivoice_env\Scripts\python.exe -c "import sys; sys.path.insert(0, r'D:\LLM\src'); from mini_gpt.omnivoice_tts_server import synthesize_fast; print(synthesize_fast('Chào anh, hôm nay em nói tiếng Việt và English thật tự nhiên nhé.', 'ai_vy', 1.0, voice_style_id='whisper'))"
~~~

Keep each printed temporary WAV path until it has been played. Record only pass/fail, profile, preset, and timing; never commit raw audio or spoken text. If a non-default preset has broken pronunciation, lost clone identity, or obvious artifacts, retain it as optional and report the listening result rather than changing the default.

**Observed 2026-08-07:** The full Python suite reported 428 passing tests with
zero failures/errors through JUnit; WinUI reported 48 passing tests; the
self-contained Release GUI publish succeeded; and `git diff --check` passed.
The running OmniVoice service had loaded its model before the feature commits,
so it was intentionally not restarted. Step 3 remains pending an explicit
reload and a real Vietnamese listening decision.

- [x] **Step 4: Update documents with actual results only.**

Add a short Voice subsection to README.md that lists the preset labels, says Giữ giọng gốc is default and sends no style instruction, and repeats that the setting never becomes chat/history/memory text. Update the spec and this plan with actual automated outcomes. Mark human listening acceptance pending unless real audio was played and confirmed.

- [x] **Step 5: Commit the finished feature, documentation, and plan.**

~~~powershell
git add README.md docs/superpowers/specs/2026-08-07-omnivoice-voice-style-presets-design.md docs/superpowers/plans/2026-08-07-omnivoice-voice-style-presets.md
git add src/mini_gpt/voice_style.py src/mini_gpt/_server_shared.py src/mini_gpt/routers/settings.py src/mini_gpt/routers/voice.py src/mini_gpt/voice_stream.py src/mini_gpt/omnivoice_tts_server.py
git add tests/test_voice_style.py tests/test_server_api.py tests/test_voice_stream.py tests/test_omnivoice_tts_server.py
git add gui-winui3/Models/Models.cs gui-winui3/Services/ApiService.cs gui-winui3/Views/VoiceView.xaml gui-winui3/Views/VoiceView.xaml.cs gui-winui3.Tests/SettingsDtoTests.cs
git commit -m "feat: add OmniVoice voice style presets"
~~~

Expected: the feature commit excludes build.bat and temporary listening WAV files.
