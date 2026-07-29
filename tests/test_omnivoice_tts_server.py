from __future__ import annotations

from pathlib import Path
import sys

import omnivoice_tts_server as server


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
    server.warm_model("yen_nhi")

    assert [prompt["ref_text"] for prompt in model.prompts] == [
        "ru transcript",
        "yen_nhi transcript",
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
    server._voice_clone_prompts["yen_nhi"] = object()
    server.release_model()

    assert server._model is None
    assert server._voice_clone_prompts == {}


def test_http_contract_does_not_load_for_status_and_validates_tts(tmp_path, monkeypatch) -> None:
    server.release_model()
    audio = tmp_path / "speech.wav"
    audio.write_bytes(b"RIFF")
    app = server.create_app(synthesize=lambda text, profile: str(audio))
    client = app.test_client()

    assert client.get("/status").get_json() == {"model_loaded": False, "pid": server.os.getpid()}
    assert client.post("/tts", json={"voice_profile": "ru"}).status_code == 400

    response = client.post("/tts", json={"text": "xin chào", "voice_profile": "yen_nhi"})

    assert response.status_code == 200
    assert response.get_json()["audio_path"] == str(audio)
    assert isinstance(response.get_json()["synthesis_ms"], int)


def test_http_reports_synthesis_error() -> None:
    app = server.create_app(synthesize=lambda text, profile: (_ for _ in ()).throw(RuntimeError("broken")))
    response = app.test_client().post("/tts", json={"text": "xin chào", "voice_profile": "ru"})

    assert response.status_code == 500
    assert response.get_json() == {"error": "broken"}


def test_synthesis_uses_the_selected_20_step_vietnamese_trial_settings(tmp_path, monkeypatch) -> None:
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
    monkeypatch.setattr(server.tempfile, "NamedTemporaryFile", lambda **kwargs: type("Output", (), {
        "name": str(tmp_path / "speech.wav"), "close": lambda self: None,
    })())
    server._voice_clone_prompts.clear()
    server._voice_clone_prompts.update({"ru": "ru-prompt", "yen_nhi": "yen-prompt"})

    ru_audio = server.synthesize_fast("Chào anh.", "ru")
    yen_audio = server.synthesize_fast("Chào em.", "yen_nhi")

    assert Path(ru_audio).is_file()
    assert Path(yen_audio).is_file()
    assert model.calls == [
        {
            "text": "Chào anh.",
            "voice_clone_prompt": "ru-prompt",
            "language": "Vietnamese",
            "normalize_text": True,
            "num_step": 20,
        },
        {
            "text": "Chào em.",
            "voice_clone_prompt": "yen-prompt",
            "language": "Vietnamese",
            "normalize_text": True,
            "num_step": 20,
        },
    ]
