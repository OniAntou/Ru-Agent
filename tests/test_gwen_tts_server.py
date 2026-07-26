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
    assert response.get_json() == {"audio_path": str(output)}
