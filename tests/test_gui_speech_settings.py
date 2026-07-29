from __future__ import annotations

from mini_gpt.speech_runtime import (
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


def test_gui_speech_settings_round_trip_the_explicit_offline_directory() -> None:
    selected = speech_settings_from_mapping(
        {"speech_engine": "sherpa-onnx", "speech_model_dir": "D:/LLM/models/sherpa"}
    )

    assert selected.engine_id == "sherpa-onnx"
    assert selected.model_dir is not None
    assert str(selected.model_dir).endswith("models\\sherpa")
