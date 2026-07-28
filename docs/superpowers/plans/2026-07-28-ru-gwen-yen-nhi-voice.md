# Ru Gwen Yến Nhi voice implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (\`- [ ]\`) syntax for tracking.

**Goal:** Give Ru a persisted Yến Nhi Gwen voice choice, retain Ru as the default, and remove Edge TTS from the product.

**Architecture:** The Gwen server owns two fixed local reference profiles, \`ru\` and \`yen_nhi\`, plus a cached clone prompt per profile. The GUI presents only a voice selector and sends its stable profile identifier to the profile-aware service for every reply. Cloud warms Gwen during cloud generation; local inference releases Gwen before Llama, then speech loads Gwen afterward. A setup script downloads the official Yến Nhi WAV exactly once.

**Tech Stack:** Python 3.13, Flask, \`qwen_tts\`, PyTorch CUDA, Tkinter, urllib, pytest.

## Global Constraints

- Keep \`Ru (Tsukuyomi)\` as the default for new and legacy settings.
- The server accepts only \`ru\` and \`yen_nhi\`; it must never accept an arbitrary path.
- Use the exact upstream Yến Nhi transcript and local WAV asset.
- Preserve the ordered speech queue and VTube \`is_speaking\` lifecycle.
- GUI speech requests always use the profile-aware fast server path; do not use the legacy worker fallback because it cannot honor the selected profile.
- Remove all product code and documentation references to Edge TTS.
- Use \`gwen_env\` for server tests and \`.venv\` for GUI tests.

---

## File structure

- \`gwen_tts_server.py\`: validates profiles and caches clone prompts by profile ID.
- \`scripts/download_gwen_yen_nhi_voice.py\`: fetches the upstream WAV once into \`data\\voices\\gwen\\yen_nhi.wav\`.
- \`src/mini_gpt/gui.py\`: persists the selected profile, removes Edge TTS, and sends profile IDs.
- \`tests/test_gwen_tts_server.py\`: profile validation, warmup cache and release coverage.
- \`tests/test_gui.py\`: request payload and GUI-choice coverage.
- \`README.md\`, \`docs/ANTIGRAVITY_HANDOFF.md\`: current voice setup and behavior.

### Task 1: Profile-aware Gwen service and Yến Nhi setup

**Files:**

- Create: \`scripts/download_gwen_yen_nhi_voice.py\`
- Modify: \`gwen_tts_server.py:14-150\`
- Modify: \`tests/test_gwen_tts_server.py\`

**Interfaces:**

- Produces \`warm_model(profile: str = "ru") -> dict[str, bool]\`.
- Produces \`synthesize_fast(text: str, profile: str = "ru") -> str\`.
- \`POST /warmup\` and \`POST /tts\` accept \`voice_profile\` in their JSON body.

- [ ] **Step 1: Write the failing profile-cache test**

\`\`\`python
def test_warmup_caches_each_supported_profile_once(monkeypatch, tmp_path) -> None:
    import gwen_tts_server as server
    from pathlib import Path

    created: list[str] = []
    profiles = {
        "ru": (tmp_path / "ru.wav", "ru transcript"),
        "yen_nhi": (tmp_path / "yen.wav", "yen transcript"),
    }
    for audio, _text in profiles.values():
        audio.write_bytes(b"RIFF")

    class Model:
        def create_voice_clone_prompt(self, *, ref_audio, ref_text):
            created.append(f"{Path(ref_audio).name}:{ref_text}")
            return [ref_audio]

    monkeypatch.setattr(server, "VOICE_PROFILES", profiles)
    monkeypatch.setattr(server, "get_model", lambda: Model())
    monkeypatch.setattr(server, "_voice_clone_prompts", {})

    assert server.warm_model("ru") == {"ready": True}
    assert server.warm_model("yen_nhi") == {"ready": True}
    assert server.warm_model("yen_nhi") == {"ready": True}
    assert created == ["ru.wav:ru transcript", "yen.wav:yen transcript"]
\`\`\`

- [ ] **Step 2: Run the test to verify it fails**

Run: \`& .\\gwen_env\\Scripts\\python.exe -m pytest tests\\test_gwen_tts_server.py::test_warmup_caches_each_supported_profile_once -q\`

Expected: FAIL because the server has only one prompt cache.

- [ ] **Step 3: Implement fixed profiles and cache**

\`\`\`python
YEN_NHI_AUDIO = PROJECT_ROOT / "data" / "voices" / "gwen" / "yen_nhi.wav"
YEN_NHI_TEXT = (
    "sao lại không liên quan. các anh lấy vợ rồi các anh cứ đội chị lên đầu "
    "làm nóc nhà ấy, suốt ngày hỏi ý kiến các chị thì làm sao mà ra vấn đề "
    "được cho em đúng không."
)
VOICE_PROFILES = {
    "ru": (REFERENCE_AUDIO, REFERENCE_TEXT.read_text(encoding="utf-8").strip()),
    "yen_nhi": (YEN_NHI_AUDIO, YEN_NHI_TEXT),
}
_voice_clone_prompts: dict[str, object] = {}

def voice_profile(profile: str) -> tuple[Path, str]:
    try:
        audio, transcript = VOICE_PROFILES[profile]
    except KeyError as error:
        raise ValueError(f"Unsupported Gwen voice profile: {profile}") from error
    if not audio.is_file():
        raise FileNotFoundError(f"Gwen voice asset is missing: {audio}")
    return audio, transcript
\`\`\`

Make \`warm_model\` create \`_voice_clone_prompts[profile]\` only once; make
\`synthesize_fast\` use that prompt; clear the entire dictionary in
\`release_model\`. Parse \`voice_profile\` with a default of \`ru\` in both
endpoints and return the existing JSON error shape for invalid/missing assets.

- [ ] **Step 4: Add invalid-profile coverage and run server tests**

\`\`\`python
def test_tts_rejects_unknown_voice_profile() -> None:
    from gwen_tts_server import create_app

    response = create_app().test_client().post(
        "/tts", json={"text": "Xin chao", "voice_profile": "outside-file"}
    )

    assert response.status_code == 500
    assert "Unsupported Gwen voice profile" in response.get_json()["error"]
\`\`\`

Run: \`& .\\gwen_env\\Scripts\\python.exe -m pytest tests\\test_gwen_tts_server.py -q\`

Expected: all server tests pass without CUDA loading.

- [ ] **Step 5: Add and run the one-time asset downloader**

\`\`\`python
from pathlib import Path
from urllib.request import urlretrieve

PROJECT_ROOT = Path(__file__).resolve().parents[1]
TARGET = PROJECT_ROOT / "data" / "voices" / "gwen" / "yen_nhi.wav"
SOURCE = "https://raw.githubusercontent.com/ggroup-ai-lab/gwen-tts/main/data/ref_audio/yen_nhi.wav"

TARGET.parent.mkdir(parents=True, exist_ok=True)
if not TARGET.is_file() or TARGET.stat().st_size == 0:
    urlretrieve(SOURCE, TARGET)
if TARGET.stat().st_size == 0:
    raise RuntimeError("Downloaded Yến Nhi reference audio is empty.")
print(TARGET)
\`\`\`

Run: \`& .\\.venv\\Scripts\\python.exe scripts\\download_gwen_yen_nhi_voice.py\`

Expected: \`data\\voices\\gwen\\yen_nhi.wav\` exists and is non-empty.

- [ ] **Step 6: Commit the service and setup script**

\`\`\`powershell
git add -- gwen_tts_server.py scripts/download_gwen_yen_nhi_voice.py tests/test_gwen_tts_server.py
git commit -m "feat: add Yến Nhi Gwen voice profile"
\`\`\`

### Task 2: Replace Edge TTS with a persisted voice selector

**Files:**

- Modify: \`src/mini_gpt/gui.py:42-151, 311-345, 558-565, 833-846, 931-1015\`
- Modify: \`tests/test_gui.py\`

**Interfaces:**

- Consumes profile IDs \`ru\` and \`yen_nhi\`.
- Changes \`request_tts_audio(text, fast_mode=False, voice_profile="ru", *, post=None) -> str\`.
- Changes \`request_gwen_control(path, payload=None, *, post=None) -> dict[str, bool]\`.

- [ ] **Step 1: Write the failing request-payload test**

\`\`\`python
def test_request_tts_audio_sends_selected_voice_profile(tmp_path) -> None:
    audio = tmp_path / "voice.wav"
    audio.write_bytes(b"RIFF")
    captured: dict[str, object] = {}

    class Response:
        status_code = 200
        @staticmethod
        def json(): return {"audio_path": str(audio)}

    assert gui.request_tts_audio(
        "Xin chao", voice_profile="yen_nhi",
        post=lambda _url, **kwargs: captured.update(kwargs) or Response(),
    ) == str(audio)
    assert captured["json"] == {
        "text": "Xin chao", "fast_mode": False, "voice_profile": "yen_nhi"
    }
\`\`\`

- [ ] **Step 2: Run the test to verify it fails**

Run: \`& .\\.venv\\Scripts\\python.exe -m pytest tests\\test_gui.py::test_request_tts_audio_sends_selected_voice_profile -q\`

Expected: FAIL because \`request_tts_audio\` has no \`voice_profile\` argument.

- [ ] **Step 3: Implement the selector and remove Edge TTS**

\`\`\`python
VOICE_PROFILE_OPTIONS = {
    "Ru (Tsukuyomi)": "ru",
    "Yến Nhi (Gwen)": "yen_nhi",
}
DEFAULT_VOICE_PROFILE = "ru"

saved_profile = saved_settings.get("voice_profile", DEFAULT_VOICE_PROFILE)
if saved_profile not in VOICE_PROFILE_OPTIONS.values():
    saved_profile = DEFAULT_VOICE_PROFILE
self.voice_profile_var = tk.StringVar(value=saved_profile)
\`\`\`

Persist \`voice_profile\` rather than \`tts_engine\`. Replace the \`TTS Engine\`
combobox with a \`Voice\` combobox containing
\`tuple(VOICE_PROFILE_OPTIONS)\`; map its label to the stable profile ID before
each warmup and speech request. Remove the \`edge_tts\` import/path,
\`voice_name\`, and every \`tts_engine_var\` reference. Send the chosen profile
to both \`/warmup\` and every chunk \`/tts\` request, always with \`fast_mode=True\`.

- [ ] **Step 4: Add GUI-choice coverage and run GUI tests**

\`\`\`python
def test_gui_offers_only_gwen_voice_profiles() -> None:
    assert gui.VOICE_PROFILE_OPTIONS == {
        "Ru (Tsukuyomi)": "ru",
        "Yến Nhi (Gwen)": "yen_nhi",
    }
    assert "Edge TTS" not in Path(gui.__file__).read_text(encoding="utf-8")
\`\`\`

Run: \`& .\\.venv\\Scripts\\python.exe -m pytest tests\\test_gui.py -q\`

Expected: all GUI tests pass without Tkinter, CUDA, a server, or real audio.

- [ ] **Step 5: Commit the GUI change**

\`\`\`powershell
git add -- src/mini_gpt/gui.py tests/test_gui.py
git commit -m "feat: let Ru switch between Gwen voice profiles"
\`\`\`

### Task 3: Update docs and prove the complete path

**Files:**

- Modify: \`README.md:112-151\`
- Modify: \`docs/ANTIGRAVITY_HANDOFF.md:58-76\`

- [ ] **Step 1: Update setup documentation**

Document the two voice choices, the one-time official Yến Nhi setup command,
and that Edge TTS no longer exists. Keep cloud warmup and local GPU release
documentation accurate.

\`\`\`powershell
& D:\\LLM\\.venv\\Scripts\\python.exe D:\\LLM\\scripts\\download_gwen_yen_nhi_voice.py
\`\`\`

- [ ] **Step 2: Run focused verification**

\`\`\`powershell
& .\\.venv\\Scripts\\python.exe -m pytest tests\\test_gui.py tests\\test_tts_chunks.py -q
& .\\gwen_env\\Scripts\\python.exe -m pytest tests\\test_gwen_tts_server.py -q
& .\\.venv\\Scripts\\python.exe -m compileall -q src\\mini_gpt gwen_tts_server.py scripts\\download_gwen_yen_nhi_voice.py
rg -n -i "edge_tts|Edge TTS" src\\mini_gpt gwen_tts_server.py README.md
git diff --check
\`\`\`

Expected: tests and compilation pass; the search has no product or README match.

- [ ] **Step 3: Perform live acceptance**

1. Run the downloader once and restart the Gwen service/GUI.
2. With a cloud backend, speak one short Vietnamese reply as Ru and then the
   same reply as Yến Nhi.
3. Confirm both lip-sync, the second voice is Yến Nhi, and later requests are
   warm after each profile has first loaded.
4. Switch to GGUF, send a local reply, and confirm local inference receives
   the GPU before selected-profile speech resumes.

- [ ] **Step 4: Commit documentation**

\`\`\`powershell
git add -- README.md docs/ANTIGRAVITY_HANDOFF.md
git commit -m "docs: explain Ru Gwen voice choices"
\`\`\`
