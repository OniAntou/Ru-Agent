from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

import speech_recognition as sr

SPEECH_ENGINE_GOOGLE = "google"
SPEECH_ENGINE_LABELS = {
    "Google (online)": SPEECH_ENGINE_GOOGLE,
}
_RECOGNITION_TIMEOUT_SECONDS = 15.0
_MAX_TRANSCRIPT_CHARS = 100_000
_MAX_MODEL_DIR_CHARS = 1_024


class SpeechRecognitionError(RuntimeError):
    """A user-safe failure while turning one captured utterance into text."""


class SpeechConfigurationError(SpeechRecognitionError):
    pass


class SpeechNoMatchError(SpeechRecognitionError):
    pass


class SpeechServiceError(SpeechRecognitionError):
    pass


@dataclass(frozen=True, slots=True)
class CapturedAudio:
    raw_data: bytes
    sample_rate_hz: int = 16_000
    sample_width_bytes: int = 2


@dataclass(frozen=True, slots=True)
class SpeechEngineConfig:
    engine_id: str = SPEECH_ENGINE_GOOGLE
    model_dir: Path | None = None
    language: str = "vi-VN"




class SpeechRecognizer(Protocol):
    engine_id: str

    def recognize(self, audio: CapturedAudio) -> str: ...


def normalize_transcript(text: str) -> str:
    if len(text) > _MAX_TRANSCRIPT_CHARS:
        raise SpeechConfigurationError("Provider transcript exceeds the size limit.")
    normalized = " ".join(text.split())
    if not normalized:
        raise SpeechNoMatchError("Could not understand audio.")
    return normalized


class GoogleSpeechRecognizer:
    engine_id = SPEECH_ENGINE_GOOGLE

    def __init__(self, recognizer: sr.Recognizer, language: str) -> None:
        self._recognizer = recognizer
        self._language = language
        if getattr(recognizer, "timeout", None) is None:
            recognizer.timeout = _RECOGNITION_TIMEOUT_SECONDS

    def recognize(self, audio: CapturedAudio) -> str:
        source_audio = sr.AudioData(
            audio.raw_data,
            audio.sample_rate_hz,
            audio.sample_width_bytes,
        )
        try:
            text = self._recognizer.recognize_google(
                source_audio,
                language=self._language,
            )
        except sr.UnknownValueError as error:
            raise SpeechNoMatchError("Could not understand audio.") from error
        except (sr.RequestError, TimeoutError) as error:
            raise SpeechServiceError("Google Speech request failed.") from error
        return normalize_transcript(text)




def build_speech_recognizer(
    config: SpeechEngineConfig,
    *,
    google_recognizer: sr.Recognizer | None = None,
) -> SpeechRecognizer:
    if config.engine_id == SPEECH_ENGINE_GOOGLE:
        if google_recognizer is None:
            google_recognizer = sr.Recognizer()
        elif not callable(getattr(google_recognizer, "recognize_google", None)):
            raise TypeError("google_recognizer must expose a callable recognize_google method.")
        return GoogleSpeechRecognizer(google_recognizer, config.language)
    raise SpeechConfigurationError("Select a valid speech input engine.")


def speech_settings_from_mapping(settings: Mapping[str, object]) -> SpeechEngineConfig:
    engine_id = settings.get("speech_engine")
    if not isinstance(engine_id, str) or not engine_id.strip():
        engine_id = SPEECH_ENGINE_GOOGLE
    elif engine_id != SPEECH_ENGINE_GOOGLE:
        raise SpeechConfigurationError(f"Unsupported speech engine: {engine_id!r}.")
    model_dir_value = settings.get("speech_model_dir")
    model_dir: Path | None = None
    if isinstance(model_dir_value, str) and model_dir_value.strip():
        cleaned = model_dir_value.strip()
        if len(cleaned) > _MAX_MODEL_DIR_CHARS or "\x00" in cleaned:
            raise SpeechConfigurationError("Invalid speech model directory.")
        model_dir = Path(cleaned)
    return SpeechEngineConfig(engine_id=engine_id, model_dir=model_dir)


def speech_settings_for_persistence(config: SpeechEngineConfig) -> dict[str, str]:
    return {
        "speech_engine": config.engine_id,
        "speech_model_dir": str(config.model_dir) if config.model_dir is not None else "",
    }



