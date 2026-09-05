from __future__ import annotations

from pathlib import Path

import pytest
import speech_recognition as sr

from ru_runtime.speech.runtime import (
    CapturedAudio,
    SpeechConfigurationError,
    SpeechEngineConfig,
    SpeechNoMatchError,
    SpeechServiceError,
    build_speech_recognizer,
    normalize_transcript,
    speech_settings_for_persistence,
    speech_settings_from_mapping,
)


class FakeGoogleRecognizer:
    timeout: float | None = None

    def recognize_google(self, _audio: object, *, language: str) -> str:
        assert language == "vi-VN"
        return "  Chao   anh  "


def test_google_adapter_normalizes_the_existing_vi_vn_path() -> None:
    recognizer = build_speech_recognizer(
        SpeechEngineConfig(), google_recognizer=FakeGoogleRecognizer()
    )

    assert recognizer.recognize(CapturedAudio(b"pcm")) == "Chao anh"


def test_google_adapter_sets_a_bounded_provider_timeout() -> None:
    provider = FakeGoogleRecognizer()

    build_speech_recognizer(SpeechEngineConfig(), google_recognizer=provider)

    assert provider.timeout == 15.0


def test_unsupported_engine_is_a_configuration_error(tmp_path: Path) -> None:
    with pytest.raises(SpeechConfigurationError, match="Select a valid speech input engine"):
        build_speech_recognizer(SpeechEngineConfig(engine_id="sherpa-onnx", model_dir=tmp_path))


def test_speech_settings_reject_unknown_engine_and_persist_round_trip() -> None:
    with pytest.raises(SpeechConfigurationError, match="Unsupported speech engine"):
        speech_settings_from_mapping(
            {"speech_engine": "sherpa-onnx", "speech_model_dir": "D:/LLM/models/sherpa"}
        )

    selected = speech_settings_from_mapping(
        {"speech_engine": "google", "speech_model_dir": "D:/LLM/models/sherpa"}
    )
    assert selected.engine_id == "google"
    assert selected.model_dir == Path("D:/LLM/models/sherpa")
    assert speech_settings_for_persistence(selected) == {
        "speech_engine": "google",
        "speech_model_dir": str(Path("D:/LLM/models/sherpa")),
    }
    assert speech_settings_from_mapping({}).engine_id == "google"


def test_empty_recognition_is_a_no_match_error() -> None:
    assert normalize_transcript("  Ru\n\t") == "Ru"
    with pytest.raises(SpeechNoMatchError):
        normalize_transcript(" \n ")


def test_google_unknown_value_is_a_no_match_error() -> None:
    class UnknownGoogleRecognizer:
        def recognize_google(self, _audio: object, *, language: str) -> str:
            raise sr.UnknownValueError()

    recognizer = build_speech_recognizer(
        SpeechEngineConfig(), google_recognizer=UnknownGoogleRecognizer()
    )

    with pytest.raises(SpeechNoMatchError, match="Could not understand"):
        recognizer.recognize(CapturedAudio(b"pcm"))


def test_google_request_error_does_not_expose_provider_details() -> None:
    class FailingGoogleRecognizer:
        def recognize_google(self, _audio: object, *, language: str) -> str:
            raise sr.RequestError("provider request included a secret")

    recognizer = build_speech_recognizer(
        SpeechEngineConfig(), google_recognizer=FailingGoogleRecognizer()
    )

    with pytest.raises(SpeechServiceError, match="Google Speech request failed"):
        recognizer.recognize(CapturedAudio(b"pcm"))


def test_google_timeout_is_a_request_service_error() -> None:
    class BrokenGoogleRecognizer:
        def recognize_google(self, _audio: object, *, language: str) -> str:
            raise TimeoutError("recognition timed out")

    recognizer = build_speech_recognizer(
        SpeechEngineConfig(), google_recognizer=BrokenGoogleRecognizer()
    )

    with pytest.raises(SpeechServiceError, match="Google Speech request failed"):
        recognizer.recognize(CapturedAudio(b"pcm"))
