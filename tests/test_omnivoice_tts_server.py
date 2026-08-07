from __future__ import annotations

from pathlib import Path
import sys

from mini_gpt import omnivoice_tts_server as server


def test_run_server_refuses_a_second_heavy_instance(monkeypatch) -> None:
    monkeypatch.setattr(server, "_acquire_single_instance", lambda: False)
    monkeypatch.setattr(
        server,
        "create_app",
        lambda: (_ for _ in ()).throw(AssertionError("second instance must not create the app")),
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
        Path(server.__file__).resolve().parents[2]
        / "data"
        / "voices"
        / "omnivoice"
        / "ai_vy.wav"
    )
    monkeypatch.setattr(Path, "is_file", lambda path: path == expected)

    audio, transcript = server.voice_profile("ai_vy")

    assert audio == expected
    assert transcript == server.AI_VY_TEXT


def test_http_contract_does_not_load_for_status_and_validates_tts(tmp_path, monkeypatch) -> None:
    server.release_model()
    audio = tmp_path / "speech.wav"
    audio.write_bytes(b"RIFF")
    app = server.create_app(
        synthesize=lambda text, profile, speed, voice_style_id: str(audio)
    )
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
        synthesize=lambda text, profile, speed, voice_style_id: (
            _ for _ in ()
        ).throw(RuntimeError("broken"))
    )
    response = app.test_client().post("/tts", json={"text": "xin chào", "voice_profile": "ru"})

    assert response.status_code == 500
    assert response.get_json() == {"error": "Synthesis failed. Check the server logs."}


def test_http_tts_passes_only_validated_voice_style(tmp_path) -> None:
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
            "voice_style_id": "whisper",
        },
    )
    invalid = client.post(
        "/tts",
        json={"text": "xin chào", "voice_style_id": "free-form"},
    )

    assert response.status_code == 200
    assert calls == [("xin chào", "ai_vy", 1.05, "whisper")]
    assert invalid.status_code == 400
    assert invalid.get_json() == {"error": "Unsupported OmniVoice voice style."}


def test_synthesis_uses_the_selected_16_step_vietnamese_trial_settings(tmp_path, monkeypatch) -> None:
    class FakeInferenceMode:
        def __enter__(self):
            return self

        def __exit__(self, exc_type, exc_value, traceback) -> None:
            return None

    class FakeTorch:
        @staticmethod
        def inference_mode() -> FakeInferenceMode:
            return FakeInferenceMode()

    class FakeSoundFile:
        @staticmethod
        def write(path: str, audio, sample_rate: int) -> None:
            Path(path).write_bytes(b"RIFF")

    class FakeModel:
        def __init__(self) -> None:
            self.calls: list[dict[str, object]] = []

        def generate(self, **kwargs):
            self.calls.append(kwargs)
            return [[0.0, 0.0]]

    model = FakeModel()
    monkeypatch.setitem(sys.modules, "torch", FakeTorch())
    monkeypatch.setitem(sys.modules, "soundfile", FakeSoundFile())
    monkeypatch.setattr(server, "warm_model", lambda profile: {"ready": True})
    monkeypatch.setattr(server, "get_model", lambda: model)
    monkeypatch.setattr(server, "apply_voice_speed", lambda audio, speed: audio)
    monkeypatch.setattr(server.tempfile, "NamedTemporaryFile", lambda **kwargs: type("Output", (), {
        "name": str(tmp_path / "speech.wav"), "close": lambda self: None,
    })())
    server._voice_clone_prompts.clear()
    server._voice_clone_prompts.update({"ru": "ru-prompt", "ai_vy": "ai-vy-prompt"})

    ru_audio = server.synthesize_fast("Chào anh.", "ru")
    ai_vy_audio = server.synthesize_fast("Chào em.", "ai_vy")
    low_pitch_audio = server.synthesize_fast(
        "Chào anh.", "ru", voice_style_id="low_pitch"
    )

    assert Path(ru_audio).is_file()
    assert Path(ai_vy_audio).is_file()
    assert Path(low_pitch_audio).is_file()
    assert model.calls == [
        {
            "text": "Chào anh.",
            "voice_clone_prompt": "ru-prompt",
            "language": "Vietnamese",
            "normalize_text": True,
            "num_step": 16,
        },
        {
            "text": "Chào em.",
            "voice_clone_prompt": "ai-vy-prompt",
            "language": "Vietnamese",
            "normalize_text": True,
            "num_step": 16,
        },
        {
            "text": "Chào anh.",
            "voice_clone_prompt": "ru-prompt",
            "language": "Vietnamese",
            "normalize_text": True,
            "num_step": 16,
            "instruct": "low pitch",
        },
    ]


def test_apply_voice_speed_mono_shape_is_1d() -> None:
    import numpy as np

    waveform = server.apply_voice_speed(np.zeros((1, 2400), dtype=np.float32), 1.0)
    assert waveform.ndim == 1
    assert waveform.shape[0] == 2400


def test_synthesize_fast_writes_1d_waveform(tmp_path, monkeypatch) -> None:
    import numpy as np

    class FakeModel:
        def create_voice_clone_prompt(self, **kwargs: str) -> str:
            return "prompt"

        def generate(self, **kwargs: object) -> list[object]:
            return [np.zeros((1, 2400), dtype=np.float32)]

    server.release_model()
    monkeypatch.setattr(server, "get_model", lambda: FakeModel())
    server._voice_clone_prompts.clear()
    server._voice_clone_prompts.update({"ru": "ru-prompt"})
    writes: list[np.ndarray] = []

    def fake_write(name: str, data: object, samplerate: int) -> None:
        writes.append(np.asarray(data))
        Path(name).write_bytes(b"RIFF\x00\x00\x00\x00WAVE")

    monkeypatch.setattr(server, "warm_model", lambda profile: {"ready": True})
    monkeypatch.setattr("soundfile.write", fake_write)
    monkeypatch.setattr(
        server.tempfile, "NamedTemporaryFile", lambda **kwargs: type(
            "Output", (), {"name": str(tmp_path / "speech.wav"), "close": lambda self: None}
        )()
    )

    path = server.synthesize_fast("Chào anh.", "ru", speed=1.0)

    assert Path(path).is_file()
    assert writes, "soundfile.write must be called"
    assert writes[0].ndim == 1, "waveform must be 1D for libsndfile"


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

    class FakeSoundFile:
        @staticmethod
        def write(path: str, data: object, samplerate: int) -> None:
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
            "Output", (), {"name": str(tmp_path / "speech.wav"), "close": lambda self: None}
        )(),
    )
    server._voice_clone_prompts.clear()
    server._voice_clone_prompts.update({"ru": "ru-prompt"})
    events: list[tuple[str, float]] = []

    path = server.synthesize_fast(
        "Chào anh.", "ru", timing=lambda name, value: events.append((name, value))
    )

    assert Path(path).is_file()
    assert [name for name, _ in events] == ["warmup_ms", "generate_ms", "write_ms", "total_ms"]
    assert all(isinstance(value, float) and value >= 0 for _, value in events)
