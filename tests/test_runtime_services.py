from __future__ import annotations

import base64
import json
import logging
from pathlib import Path
from types import SimpleNamespace

import pytest

from ru_runtime.worker.v1 import runtime_services as shared
from ru_runtime.worker.v1 import vts as runtime_vts
from ru_runtime.worker.v1.http import dispatch_runtime_request

ROOT = Path(__file__).resolve().parents[1]


def test_voice_start_waits_for_an_existing_owned_process(monkeypatch) -> None:
    process = SimpleNamespace(poll=lambda: None)
    monkeypatch.setattr(shared, "_VOICE_PROCESS", process)
    monkeypatch.setattr(shared, "voice_available", lambda: False)
    monkeypatch.setattr(shared, "VOICE_START_TIMEOUT_SECONDS", 0.0)

    with pytest.raises(RuntimeError, match="existing process"):
        shared.ensure_voice_service()


def test_runtime_service_modules_have_no_web_framework_imports() -> None:
    for name in (
        "http.py",
        "status.py",
        "voice.py",
        "vts.py",
        "observation.py",
    ):
        source = (
            (ROOT / "src" / "ru_runtime" / "worker" / "v1" / name)
            .read_text(encoding="utf-8")
            .casefold()
        )
        assert "ru_runtime.routers" not in source
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
        "subtitle_color": "#FFFFFF",
        "subtitle_size": "medium",
        "subtitle_duration": 2.0,
        "subtitle_pos_mode": "auto",
        "subtitle_pos_x": -1,
        "subtitle_pos_y": -1,
    }


def test_transcribe_returns_text_without_exposing_audio(monkeypatch) -> None:
    from ru_runtime.worker.v1 import voice

    class Recognizer:
        def recognize(self, audio):
            assert audio.sample_rate_hz == 16_000
            return "xin chao"

    monkeypatch.setattr(
        voice,
        "build_speech_recognizer",
        lambda _config: Recognizer(),
    )
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


def test_apply_saved_settings_migrates_retired_gguf_to_opencode(monkeypatch, tmp_path) -> None:
    settings_file = tmp_path / "settings.json"
    settings_file.write_text(
        '{"backend": "llama.cpp", "model": "LFM2.5-2.6B-Q4_K_M.gguf"}',
        encoding="utf-8",
    )
    monkeypatch.setattr(shared, "SETTINGS_FILE", settings_file)
    loaded: dict = {}

    def fake_load(**kwargs) -> str:
        loaded.update(kwargs)
        return "Ready"

    monkeypatch.setattr(shared.core.runner, "load", fake_load)
    selected: list[str] = []

    def fake_select(backend: str) -> None:
        selected.append(backend)

    monkeypatch.setattr(shared.core.runner, "select_backend", fake_select)

    result = shared.apply_saved_settings()

    assert selected == ["OpenCode Zen"]
    assert loaded["model_name"] == "deepseek-v4-flash-free"
    assert result == "Ready"


def test_apply_saved_settings_loads_custom_key_for_custom_backend(monkeypatch, tmp_path) -> None:
    settings_file = tmp_path / "settings.json"
    settings_file.write_text(
        '{"backend": "Custom OpenAI", "custom_model": "my-model", "custom_url": "http://localhost:9000/v1"}',
        encoding="utf-8",
    )
    monkeypatch.setattr(shared, "SETTINGS_FILE", settings_file)
    monkeypatch.setattr(
        shared.core.custom_cred,
        "get_key",
        lambda: "private-custom-key",
    )
    loaded: dict = {}

    def fake_load(**kwargs) -> str:
        loaded.update(kwargs)
        return "Ready"

    monkeypatch.setattr(shared.core.runner, "load", fake_load)
    monkeypatch.setattr(
        shared.core.runner,
        "select_backend",
        lambda backend: None,
    )

    result = shared.apply_saved_settings()

    assert loaded["api_key"] == "private-custom-key"
    assert loaded["model_name"] == "my-model"
    assert loaded["base_url"] == "http://localhost:9000/v1"
    assert result == "Ready"


def test_apply_saved_settings_never_logs_credential_exception_text(
    monkeypatch, tmp_path, caplog
) -> None:
    settings_file = tmp_path / "settings.json"
    settings_file.write_text(
        '{"backend": "Custom OpenAI", "custom_model": "model", "custom_url": "http://localhost:9000/v1"}',
        encoding="utf-8",
    )
    monkeypatch.setattr(shared, "SETTINGS_FILE", settings_file)
    secret = "https://user:password@provider.example/?api_key=credential"

    def explode() -> str:
        raise RuntimeError(secret)

    monkeypatch.setattr(shared.core.custom_cred, "get_key", explode)
    monkeypatch.setattr(shared.core.runner, "select_backend", lambda _backend: None)
    monkeypatch.setattr(shared.core.runner, "load", lambda **_kwargs: "Ready")

    with caplog.at_level(logging.WARNING, logger="ru_runtime.worker.v1.runtime_services"):
        assert shared.apply_saved_settings() == "Ready"

    assert "password" not in caplog.text
    assert "api_key" not in caplog.text
    assert "provider.example" not in caplog.text


def test_effective_settings_never_inherits_a_retired_gguf_model(monkeypatch, tmp_path) -> None:
    settings_file = tmp_path / "settings.json"
    settings_file.write_text(
        '{"backend": "OpenCode Zen", "model": "LFM2.5-2.6B-Q4_K_M.gguf", "opencode_model": ""}',
        encoding="utf-8",
    )
    monkeypatch.setattr(shared, "SETTINGS_FILE", settings_file)

    merged = shared.effective_settings()

    assert merged["opencode_model"] == "deepseek-v4-flash-free"


def test_malformed_runtime_json_returns_bounded_422() -> None:
    response = dispatch_runtime_request(
        "POST",
        "/voice/tts",
        "application/json",
        '{"text":',
    )

    assert response.status_code == 422
    assert json.loads(response.body) == {"detail": "Invalid runtime request."}
