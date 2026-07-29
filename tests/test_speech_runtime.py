from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace

import pytest
import speech_recognition as sr

from mini_gpt.speech_runtime import (
    CapturedAudio,
    SpeechConfigurationError,
    SpeechEngineConfig,
    SpeechEngineUnavailable,
    SpeechNoMatchError,
    SpeechServiceError,
    SherpaOnnxRecognizer,
    build_speech_recognizer,
    normalize_transcript,
)


class FakeGoogleRecognizer:
    def recognize_google(self, _audio: object, *, language: str) -> str:
        assert language == "vi-VN"
        return "  Chao   anh  "


def test_google_adapter_normalizes_the_existing_vi_vn_path() -> None:
    recognizer = build_speech_recognizer(
        SpeechEngineConfig(), google_recognizer=FakeGoogleRecognizer()
    )

    assert recognizer.recognize(CapturedAudio(b"pcm")) == "Chao anh"


def test_sherpa_requires_the_complete_local_model_layout(tmp_path: Path) -> None:
    with pytest.raises(SpeechConfigurationError, match="tokens.txt"):
        build_speech_recognizer(
            SpeechEngineConfig(engine_id="sherpa-onnx", model_dir=tmp_path)
        )


def test_sherpa_missing_optional_package_has_a_recovery_message(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    for name in ("tokens.txt", "encoder.onnx", "decoder.onnx", "joiner.onnx"):
        (tmp_path / name).write_bytes(b"model")

    def missing_package() -> object:
        raise SpeechEngineUnavailable(
            "Sherpa-ONNX is not installed. Install Ru with the speech-offline extra."
        )

    monkeypatch.setattr("mini_gpt.speech_runtime._import_sherpa_onnx", missing_package)

    with pytest.raises(SpeechEngineUnavailable, match="speech-offline"):
        build_speech_recognizer(
            SpeechEngineConfig(engine_id="sherpa-onnx", model_dir=tmp_path)
        )


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


def test_sherpa_adapter_normalizes_a_successful_local_result(tmp_path: Path) -> None:
    for name in ("tokens.txt", "encoder.onnx", "decoder.onnx", "joiner.onnx"):
        (tmp_path / name).write_bytes(b"model")

    class FakeStream:
        result = SimpleNamespace(text="  xin   chao anh  ")

        def accept_waveform(self, sample_rate_hz: int, samples: object) -> None:
            assert sample_rate_hz == 16_000
            assert getattr(samples, "tolist")() == [0.0]

    class FakeOfflineRecognizer:
        @classmethod
        def from_transducer(cls, **kwargs: object) -> "FakeOfflineRecognizer":
            assert kwargs["provider"] == "cpu"
            return cls()

        def create_stream(self) -> object:
            return FakeStream()

        def decode_stream(self, _stream: object) -> None:
            pass

    fake_module = SimpleNamespace(OfflineRecognizer=FakeOfflineRecognizer)
    recognizer = SherpaOnnxRecognizer(
        SpeechEngineConfig(engine_id="sherpa-onnx", model_dir=tmp_path),
        sherpa_module=fake_module,
    )

    assert recognizer.recognize(CapturedAudio(b"\x00\x00")) == "xin chao anh"


def test_sherpa_accepts_the_official_vietnamese_int8_model_layout(tmp_path: Path) -> None:
    for name in ("tokens.txt", "encoder.int8.onnx", "decoder.onnx", "joiner.int8.onnx"):
        (tmp_path / name).write_bytes(b"model")

    class FakeOfflineRecognizer:
        @classmethod
        def from_transducer(cls, **kwargs: object) -> "FakeOfflineRecognizer":
            assert str(kwargs["encoder"]).endswith("encoder.int8.onnx")
            assert str(kwargs["decoder"]).endswith("decoder.onnx")
            assert str(kwargs["joiner"]).endswith("joiner.int8.onnx")
            return cls()

    fake_module = SimpleNamespace(OfflineRecognizer=FakeOfflineRecognizer)
    recognizer = SherpaOnnxRecognizer(
        SpeechEngineConfig(engine_id="sherpa-onnx", model_dir=tmp_path),
        sherpa_module=fake_module,
    )

    assert recognizer.engine_id == "sherpa-onnx"
