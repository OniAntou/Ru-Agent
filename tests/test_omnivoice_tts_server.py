from __future__ import annotations

import sys
from pathlib import Path

import ru_runtime.voice.omnivoice_server as server


def _fail_tempfile(*args, **kwargs):
    raise AssertionError("byte-based synthesis must not create a temp file")


def _fake_fdopen_writing_wav_bytes(target: Path):
    import contextlib

    @contextlib.contextmanager
    def fake_fdopen(fd: int, mode: str):
        written: list[bytes] = []
        yield _CollectingFile(written)
        assert written, "synthesize_fast must write WAV bytes to the temp file"
        target.write_bytes(b"".join(written))

    return fake_fdopen


class _CollectingFile:
    def __init__(self, written: list[bytes]) -> None:
        self._written = written

    def write(self, data: bytes) -> int:
        chunk = bytes(data)
        self._written.append(chunk)
        return len(chunk)


def test_run_server_refuses_a_second_heavy_instance(monkeypatch) -> None:
    monkeypatch.setattr(server, "_acquire_single_instance", lambda: False)
    monkeypatch.setattr(
        server,
        "create_app",
        lambda: (_ for _ in ()).throw(AssertionError("second instance must not create the app")),
    )

    assert server.run_server() == 2


def test_run_server_uses_the_managed_loopback_binding(monkeypatch) -> None:
    monkeypatch.setenv("RU_OMNIVOICE_HOST", "127.0.0.1")
    monkeypatch.setenv("RU_OMNIVOICE_PORT", "5057")
    monkeypatch.setattr(server, "_acquire_single_instance", lambda: True)
    calls: dict[str, object] = {}

    class FakeApp:
        def run(self, **kwargs: object) -> None:
            calls.update(kwargs)

    monkeypatch.setattr(server, "create_app", lambda: FakeApp())

    assert server.run_server() == 0
    assert calls == {"host": "127.0.0.1", "port": 5057, "threaded": True}


def test_run_server_rejects_a_non_loopback_binding(monkeypatch) -> None:
    monkeypatch.setenv("RU_OMNIVOICE_HOST", "0.0.0.0")
    monkeypatch.setattr(
        server,
        "_acquire_single_instance",
        lambda: (_ for _ in ()).throw(AssertionError("invalid binding must fail before locking")),
    )

    assert server.run_server() == 2


class FakeModel:
    def __init__(self) -> None:
        self.prompts: list[dict[str, str]] = []

    def create_voice_clone_prompt(self, **kwargs: str) -> dict[str, str]:
        self.prompts.append(kwargs)
        return kwargs


def test_warm_model_caches_one_prompt_per_profile(monkeypatch) -> None:
    model = FakeModel()
    monkeypatch.setattr(server, "_model", model)
    server._voice_clone_prompts.clear()
    monkeypatch.setattr(
        server,
        "voice_profile",
        lambda profile: (Path(f"D:/{profile}.wav"), f"{profile} transcript"),
    )

    server.warm_model("ru")
    server.warm_model("ru")
    server.warm_model("ai_vy")

    assert [prompt["ref_text"] for prompt in model.prompts] == [
        "ru transcript",
        "ai_vy transcript",
    ]
    server.release_model()


def test_profile_validation_and_release_clear_runtime() -> None:
    try:
        server.voice_profile("unknown")
    except ValueError as error:
        assert "Unsupported OmniVoice voice profile" in str(error)
    else:
        raise AssertionError("Unknown profile should fail")

    server._model = object()
    server._voice_clone_prompts["ru"] = object()
    server._voice_clone_prompts["ai_vy"] = object()
    server.release_model()

    assert server._model is None
    assert server._voice_clone_prompts == {}


def test_ai_vy_profile_uses_the_repository_voice_asset(monkeypatch) -> None:
    expected = (
        Path(server.__file__).resolve().parents[3] / "data" / "voices" / "omnivoice" / "ai_vy.wav"
    )
    monkeypatch.setattr(Path, "is_file", lambda path: path == expected)

    audio, transcript = server.voice_profile("ai_vy")

    assert audio == expected
    assert transcript == server.AI_VY_TEXT


def test_http_contract_does_not_load_for_status_and_validates_tts(tmp_path, monkeypatch) -> None:
    server.release_model()
    audio = tmp_path / "speech.wav"
    audio.write_bytes(b"RIFF")
    app = server.create_app(synthesize=lambda text, profile, speed, voice_style_id: str(audio))
    client = app.test_client()

    assert client.get("/status").get_json() == {"model_loaded": False}
    assert client.post("/tts", json={"voice_profile": "ru"}).status_code == 400
    assert client.post("/tts", data="not json", content_type="application/json").status_code == 400

    response = client.post("/tts", json={"text": "xin chào", "voice_profile": "ai_vy"})

    assert response.status_code == 200
    assert response.content_type == "audio/wav"
    assert response.data == b"RIFF"
    assert isinstance(int(response.headers["X-Synthesis-Ms"]), int)


def test_http_reports_synthesis_error() -> None:
    app = server.create_app(
        synthesize=lambda text, profile, speed, voice_style_id: (_ for _ in ()).throw(
            RuntimeError("broken")
        )
    )
    response = app.test_client().post("/tts", json={"text": "xin chào", "voice_profile": "ru"})

    assert response.status_code == 500
    assert response.get_json() == {"error": "Synthesis failed. Check the server logs."}


def test_http_tts_accepts_only_original_voice_style(tmp_path) -> None:
    audio = tmp_path / "speech.wav"
    audio.write_bytes(b"RIFF")
    calls: list[tuple[str, str, float, str]] = []

    def synthesize(text: str, profile: str, speed: float, voice_style_id: str) -> str:
        calls.append((text, profile, speed, voice_style_id))
        return str(audio)

    client = server.create_app(synthesize=synthesize).test_client()

    response = client.post(
        "/tts",
        json={
            "text": "xin chào",
            "voice_profile": "ai_vy",
            "speed": 1.05,
            "voice_style_id": "original",
        },
    )
    retired = [
        client.post(
            "/tts",
            json={"text": "xin chào", "voice_style_id": style_id},
        )
        for style_id in ("low_pitch", "high_pitch", "whisper")
    ]
    invalid = client.post(
        "/tts",
        json={"text": "xin chào", "voice_style_id": "free-form"},
    )

    assert response.status_code == 200
    assert calls == [("xin chào", "ai_vy", 1.05, "original")]
    assert [item.status_code for item in retired] == [400, 400, 400]
    assert [item.get_json() for item in retired] == [
        {"error": "Unsupported OmniVoice voice style."},
        {"error": "Unsupported OmniVoice voice style."},
        {"error": "Unsupported OmniVoice voice style."},
    ]
    assert invalid.status_code == 400
    assert invalid.get_json() == {"error": "Unsupported OmniVoice voice style."}


def test_synthesis_uses_native_speed_without_local_waveform_effects(tmp_path, monkeypatch) -> None:
    class FakeInferenceMode:
        def __enter__(self):
            return self

        def __exit__(self, exc_type, exc_value, traceback) -> None:
            return None

    class FakeTorch:
        @staticmethod
        def inference_mode() -> FakeInferenceMode:
            return FakeInferenceMode()

    class FakeModel:
        def __init__(self) -> None:
            self.calls: list[dict[str, object]] = []

        def generate(self, **kwargs):
            self.calls.append(kwargs)
            return [[0.0, 0.0]]

    model = FakeModel()
    monkeypatch.setitem(sys.modules, "torch", FakeTorch())
    monkeypatch.setattr(server, "warm_model", lambda profile: {"ready": True})
    monkeypatch.setattr(server, "get_model", lambda: model)
    monkeypatch.setattr(
        server,
        "apply_voice_speed",
        lambda *_args: (_ for _ in ()).throw(
            AssertionError("synthesis must not time-stretch the completed waveform")
        ),
        raising=False,
    )
    monkeypatch.setattr(
        server,
        "apply_voice_pitch",
        lambda *_args: (_ for _ in ()).throw(
            AssertionError("synthesis must not pitch-shift the completed waveform")
        ),
        raising=False,
    )
    monkeypatch.setattr(
        server.tempfile,
        "mkstemp",
        lambda **kwargs: (999, str(tmp_path / "speech.wav")),
    )
    monkeypatch.setattr(server.os, "fdopen", _fake_fdopen_writing_wav_bytes(tmp_path / "speech.wav"))
    server._voice_clone_prompts.clear()
    server._voice_clone_prompts.update({"ru": "ru-prompt", "ai_vy": "ai-vy-prompt"})

    fast_audio = server.synthesize_fast("Chào anh.", "ru", speed=1.08)
    baseline_audio = server.synthesize_fast("Chào anh.", "ru", speed=1.0)

    assert Path(fast_audio).is_file()
    assert Path(baseline_audio).is_file()
    assert model.calls == [
        {
            "text": "Chào anh.",
            "voice_clone_prompt": "ru-prompt",
            "language": "Vietnamese",
            "normalize_text": True,
            "num_step": 16,
            "speed": 1.08,
        },
        {
            "text": "Chào anh.",
            "voice_clone_prompt": "ru-prompt",
            "language": "Vietnamese",
            "normalize_text": True,
            "num_step": 16,
        },
    ]


def test_synthesize_fast_bytes_returns_wav_bytes_without_temp_file(monkeypatch) -> None:
    import numpy as np

    def fake_generate(*args, **kwargs):
        return np.zeros((1, 2400), dtype=np.float32)

    monkeypatch.setattr(server, "_generate_source_audio", fake_generate)
    server._voice_clone_prompts.clear()
    server._voice_clone_prompts.update({"ru": "ru-prompt"})

    monkeypatch.setattr(server.tempfile, "mkstemp", _fail_tempfile)
    monkeypatch.setattr(server, "warm_model", lambda profile: {"ready": True})

    import io
    import wave as wave_module

    buffer = server.synthesize_fast_bytes("Chào anh.", "ru", speed=1.08)
    with wave_module.open(io.BytesIO(buffer), "rb") as output:
        assert output.getnchannels() == 1
        assert output.getframerate() == server.MODEL_SAMPLE_RATE
        assert output.getnframes() == 2400


def test_tts_route_returns_bytes_without_temp_file(tmp_path, monkeypatch) -> None:
    import numpy as np

    server.release_model()

    def fake_generate(*args, **kwargs):
        return np.zeros((1, 2400), dtype=np.float32)

    monkeypatch.setattr(server, "_generate_source_audio", fake_generate)
    server._voice_clone_prompts.clear()
    server._voice_clone_prompts.update({"ru": "ru-prompt"})

    monkeypatch.setattr(server.tempfile, "mkstemp", _fail_tempfile)

    client = server.create_app().test_client()
    response = client.post("/tts", json={"text": "xin chào", "voice_profile": "ru"})

    assert response.status_code == 200
    assert response.content_type == "audio/wav"
    assert response.data.startswith(b"RIFF")


def test_tts_stream_route_uses_bytes_without_temp_files(tmp_path, monkeypatch) -> None:
    import io
    import wave as wave_module

    import numpy as np

    def fake_generate(*args, **kwargs):
        return np.zeros((1, 2400), dtype=np.float32)

    monkeypatch.setattr(server, "_generate_source_audio", fake_generate)
    server._voice_clone_prompts.clear()
    server._voice_clone_prompts.update({"ru": "ru-prompt"})

    monkeypatch.setattr(server.tempfile, "mkstemp", _fail_tempfile)
    monkeypatch.setattr(server, "warm_model", lambda profile: {"ready": True})

    client = server.create_app().test_client()
    response = client.post("/tts_stream", json={"text": "xin chào", "voice_profile": "ru"})

    assert response.status_code == 200
    with wave_module.open(io.BytesIO(response.data), "rb") as output:
        assert output.getnchannels() == 1
        assert output.getframerate() == server.MODEL_SAMPLE_RATE


def test_completed_omnivoice_audio_is_written_as_a_mono_waveform() -> None:
    import numpy as np

    waveform = server._as_waveform(np.zeros((1, 2400), dtype=np.float32))
    assert waveform.ndim == 1
    assert waveform.shape[0] == 2400


def test_omnivoice_server_exposes_no_local_waveform_effects() -> None:
    assert not hasattr(server, "apply_voice_speed")
    assert not hasattr(server, "apply_voice_pitch")


def test_synthesize_fast_writes_wav_bytes_without_soundfile(tmp_path, monkeypatch) -> None:
    import io
    import wave as wave_module

    import numpy as np

    class FakeInferenceMode:
        def __enter__(self):
            return self

        def __exit__(self, exc_type, exc_value, traceback) -> None:
            return None

    class FakeTorch:
        @staticmethod
        def inference_mode() -> FakeInferenceMode:
            return FakeInferenceMode()

    class FakeModel:
        def create_voice_clone_prompt(self, **kwargs: str) -> str:
            return "prompt"

        def generate(self, **kwargs: object) -> list[object]:
            return [np.zeros((1, 2400), dtype=np.float32)]

    server.release_model()
    monkeypatch.setattr(server, "get_model", lambda: FakeModel())
    server._voice_clone_prompts.clear()
    server._voice_clone_prompts.update({"ru": "ru-prompt"})

    target = tmp_path / "speech.wav"

    def fake_mkstemp(**kwargs):
        if target.is_file():
            target.unlink()
        return 999, str(target)

    def fake_fdopen(fd: int, mode: str):
        assert fd == 999
        return target.open("wb")

    monkeypatch.setattr(server, "warm_model", lambda profile: {"ready": True})
    monkeypatch.setitem(sys.modules, "torch", FakeTorch())
    assert "soundfile" not in sys.modules
    monkeypatch.setattr(server.tempfile, "mkstemp", fake_mkstemp)
    monkeypatch.setattr(server.os, "fdopen", fake_fdopen)

    path = server.synthesize_fast("Chào anh.", "ru", speed=1.0)

    assert Path(path).is_file()
    with wave_module.open(io.BytesIO(Path(path).read_bytes()), "rb") as output:
        assert output.getnchannels() == 1
        assert output.getframerate() == server.MODEL_SAMPLE_RATE
        assert output.getnframes() == 2400


def test_synthesize_fast_reports_privacy_safe_timing(tmp_path, monkeypatch) -> None:
    import numpy as np

    class FakeInferenceMode:
        def __enter__(self):
            return self

        def __exit__(self, exc_type, exc_value, traceback) -> None:
            return None

    class FakeTorch:
        @staticmethod
        def inference_mode() -> FakeInferenceMode:
            return FakeInferenceMode()

    class FakeModel:
        def generate(self, **kwargs: object) -> list[object]:
            return [np.zeros((1, 2400), dtype=np.float32)]

    monkeypatch.setitem(sys.modules, "torch", FakeTorch())
    monkeypatch.setattr(server, "warm_model", lambda profile: {"ready": True})
    monkeypatch.setattr(server, "get_model", lambda: FakeModel())
    monkeypatch.setattr(
        server.tempfile,
        "mkstemp",
        lambda **kwargs: (999, str(tmp_path / "speech.wav")),
    )
    monkeypatch.setattr(server.os, "fdopen", _fake_fdopen_writing_wav_bytes(tmp_path / "speech.wav"))
    server._voice_clone_prompts.clear()
    server._voice_clone_prompts.update({"ru": "ru-prompt"})
    events: list[tuple[str, float]] = []

    path = server.synthesize_fast(
        "Chào anh.", "ru", timing=lambda name, value: events.append((name, value))
    )

    assert Path(path).is_file()
    assert [name for name, _ in events] == [
        "warmup_ms",
        "generate_ms",
        "write_ms",
        "total_ms",
    ]
    assert all(isinstance(value, float) and value >= 0 for _, value in events)
