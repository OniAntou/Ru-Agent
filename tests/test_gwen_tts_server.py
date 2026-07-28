from __future__ import annotations

from pathlib import Path


def test_tts_rejects_missing_text() -> None:
    from gwen_tts_server import create_app

    response = create_app().test_client().post("/tts", json={})

    assert response.status_code == 400
    assert response.get_json() == {"error": "No text provided"}


def test_tts_returns_generated_audio_path(tmp_path) -> None:
    from gwen_tts_server import create_app

    output = tmp_path / "gwen.wav"
    output.write_bytes(b"RIFF")
    app = create_app(synthesize=lambda _text: str(output))

    response = app.test_client().post("/tts", json={"text": "hello"})

    assert response.status_code == 200
    payload = response.get_json()
    assert payload["audio_path"] == str(output)
    assert isinstance(payload["synthesis_ms"], int)
    assert payload["synthesis_ms"] >= 0


def test_warmup_caches_each_supported_profile_once(monkeypatch, tmp_path) -> None:
    import gwen_tts_server as server

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


def test_tts_rejects_unknown_voice_profile() -> None:
    from gwen_tts_server import create_app

    response = create_app().test_client().post(
        "/tts", json={"text": "Xin chao", "voice_profile": "outside-file"}
    )

    assert response.status_code == 500
    assert "Unsupported Gwen voice profile" in response.get_json()["error"]


def test_release_endpoint_clears_cached_model_and_prompt(monkeypatch) -> None:
    import gwen_tts_server as server

    released: list[bool] = []
    monkeypatch.setattr(server, "release_model", lambda: released.append(True))

    response = server.create_app().test_client().post("/release")

    assert response.status_code == 200
    assert response.get_json() == {"released": True}
    assert released == [True]
