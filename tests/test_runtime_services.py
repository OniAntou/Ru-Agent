from __future__ import annotations

import base64
import json
from pathlib import Path
from types import SimpleNamespace

from mini_gpt import _server_shared as shared
from mini_gpt import runtime_vts
from mini_gpt.runtime_http import dispatch_runtime_request


ROOT = Path(__file__).resolve().parents[1]


def test_runtime_service_modules_have_no_web_framework_imports() -> None:
    for name in (
        "runtime_http.py",
        "runtime_status.py",
        "runtime_voice.py",
        "runtime_vts.py",
        "runtime_observation.py",
    ):
        source = (ROOT / "src" / "mini_gpt" / name).read_text(encoding="utf-8").casefold()
        assert "mini_gpt.routers" not in source
        assert "fastapi" not in source
        assert "starlette" not in source
        assert "pydantic" not in source


def test_voice_status_uses_saved_runtime_settings(monkeypatch) -> None:
    monkeypatch.setattr(
        shared,
        "effective_settings",
        lambda: {
            "voice_label": "Ru (OmniVoice)",
            "voice_style_id": "original",
            "voice_speed": 1.25,
            "auto_pace_enabled": False,
            "auto_read_replies": True,
            "show_subtitles": True,
        },
    )
    monkeypatch.setattr(shared, "voice_available", lambda: True)

    response = dispatch_runtime_request("GET", "/voice/status", None, "")
    payload = json.loads(response.body)

    assert payload == {
        "status": "ok",
        "available": True,
        "voice_label": "Ru (OmniVoice)",
        "voice_style_id": "original",
        "voice_speed": 1.25,
        "auto_pace_enabled": False,
        "auto_read_replies": True,
        "show_subtitles": True,
    }


def test_transcribe_returns_text_without_exposing_audio(monkeypatch) -> None:
    class Recognizer:
        def recognize(self, audio):
            assert audio.sample_rate_hz == 16_000
            return "xin chao"

    monkeypatch.setattr(shared, "build_speech_recognizer", lambda _config: Recognizer())
    audio = base64.b64encode(b"\x00\x00\x00\x00").decode("ascii")

    response = dispatch_runtime_request(
        "POST",
        "/voice/transcribe",
        "application/json",
        json.dumps({"audio_b64": audio, "sample_rate": 16_000}),
    )

    assert json.loads(response.body) == {"status": "ok", "text": "xin chao"}
    assert audio.encode() not in response.body


def test_observation_disabled_contract_is_stable() -> None:
    response = dispatch_runtime_request("GET", "/observation/status", None, "")

    assert json.loads(response.body) == {
        "status": "disabled",
        "active": False,
        "frame_count": 0,
        "vision_count": 0,
        "emitted_count": 0,
        "stale_drops": 0,
        "ms_before": 0,
        "ms_cloud": 0,
        "ms_after": 0,
        "last_motion_delta": 0.0,
        "engine": "disabled",
    }


def test_vts_status_uses_the_single_python_manager(monkeypatch) -> None:
    manager = SimpleNamespace(
        is_connected=False,
        config=SimpleNamespace(to_dict=lambda: {"sway_amplitude_idle": 10.0}),
        current_state=lambda: SimpleNamespace(value="idle"),
        last_connect_error=None,
    )
    monkeypatch.setattr(shared, "_vts_manager", lambda: manager)
    monkeypatch.setattr(
        shared,
        "_load_vts_gui_state",
        lambda: {
            "mouse_look_enabled": False,
            "mouse_look_sensitivity": 1.0,
            "overlay_enabled": False,
        },
    )
    monkeypatch.setattr(runtime_vts, "overlay_process_ids", lambda: [])

    response = dispatch_runtime_request("GET", "/vts/status", None, "")
    payload = json.loads(response.body)

    assert payload["status"] == "ok"
    assert payload["connected"] is False
    assert payload["state"] == "idle"
    assert payload["overlay_running"] is False


def test_malformed_runtime_json_returns_bounded_422() -> None:
    response = dispatch_runtime_request(
        "POST",
        "/voice/tts",
        "application/json",
        '{"text":',
    )

    assert response.status_code == 422
    assert json.loads(response.body) == {"detail": "Invalid runtime request."}
