from __future__ import annotations

from pathlib import Path

from ru_runtime.voice_runtime import (
    DEFAULT_VOICE_SELECTION,
    voice_selection_by_label,
    voice_selection_from_saved,
    voice_service_config,
)

PROJECT_ROOT = Path(__file__).resolve().parents[1]


def test_omnivoice_ai_vy_selection_and_service_config() -> None:
    selection = voice_selection_by_label("Ái Vy (OmniVoice)")

    assert (selection.engine_id, selection.profile_id) == ("omnivoice", "ai_vy")

    config = voice_service_config(selection, PROJECT_ROOT)

    assert config.base_url == "http://127.0.0.1:5052"
    assert config.python_path == PROJECT_ROOT / "omnivoice_env" / "Scripts" / "python.exe"
    assert config.server_script == PROJECT_ROOT / "src" / "ru_runtime" / "omnivoice_tts_server.py"


def test_saved_legacy_or_unknown_selection_migrates_to_gwen_ru() -> None:
    legacy = voice_selection_from_saved("Ru (Gwen)")
    unknown = voice_selection_from_saved("something else")
    non_string = voice_selection_from_saved(None)

    assert legacy == DEFAULT_VOICE_SELECTION
    assert unknown == DEFAULT_VOICE_SELECTION
    assert non_string == DEFAULT_VOICE_SELECTION
