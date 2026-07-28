from __future__ import annotations


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


def test_warmup_reuses_cached_voice_clone_prompt(monkeypatch) -> None:
    import gwen_tts_server as server

    created: list[str] = []

    class Model:
        def create_voice_clone_prompt(self, **_kwargs):
            created.append("prompt")
            return ["cached-prompt"]

    monkeypatch.setattr(server, "get_model", lambda: Model())
    monkeypatch.setattr(server, "_voice_clone_prompt", None)

    assert server.warm_model() == {"ready": True}
    assert server.warm_model() == {"ready": True}
    assert created == ["prompt"]


def test_release_endpoint_clears_cached_model_and_prompt(monkeypatch) -> None:
    import gwen_tts_server as server

    released: list[bool] = []
    monkeypatch.setattr(server, "release_model", lambda: released.append(True))

    response = server.create_app().test_client().post("/release")

    assert response.status_code == 200
    assert response.get_json() == {"released": True}
    assert released == [True]
