from __future__ import annotations

from pathlib import Path

import pytest

from mini_gpt.speech_runtime import (
    SpeechConfigurationError,
    SpeechEngineConfig,
    speech_settings_for_persistence,
    speech_settings_from_mapping,
)


def test_gui_speech_settings_default_to_google_without_storing_transcript_or_key() -> None:
    default = speech_settings_from_mapping({"api_key": "secret", "transcript": "hello"})

    assert default == SpeechEngineConfig()
    assert speech_settings_for_persistence(default) == {
        "speech_engine": "google",
        "speech_model_dir": "",
    }


def test_gui_speech_settings_reject_removed_offline_engine_and_round_trip_google() -> None:
    # Sherpa-ONNX was removed from the runtime; legacy settings must raise
    # instead of silently falling back, so callers surface a clear error.
    with pytest.raises(SpeechConfigurationError, match="Unsupported speech engine"):
        speech_settings_from_mapping(
            {"speech_engine": "sherpa-onnx", "speech_model_dir": "D:/LLM/models/sherpa"}
        )

    selected = speech_settings_from_mapping(
        {"speech_engine": "google", "speech_model_dir": "D:/LLM/models/sherpa"}
    )
    assert selected.engine_id == "google"
    assert selected.model_dir == Path("D:/LLM/models/sherpa")
    assert speech_settings_from_mapping(
        speech_settings_for_persistence(selected)
    ) == SpeechEngineConfig(engine_id="google", model_dir=Path("D:/LLM/models/sherpa"))
