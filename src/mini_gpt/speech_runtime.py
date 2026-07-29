from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from types import ModuleType
from typing import Mapping, Protocol

import numpy as np
import speech_recognition as sr


SPEECH_ENGINE_GOOGLE = "google"
SPEECH_ENGINE_SHERPA_ONNX = "sherpa-onnx"
SPEECH_ENGINE_LABELS = {
    "Google (online)": SPEECH_ENGINE_GOOGLE,
    "Sherpa-ONNX (offline)": SPEECH_ENGINE_SHERPA_ONNX,
}
_SHERPA_TOKENS_FILE = "tokens.txt"


class SpeechRecognitionError(RuntimeError):
    """A user-safe failure while turning one captured utterance into text."""


class SpeechEngineUnavailable(SpeechRecognitionError):
    pass


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


@dataclass(frozen=True, slots=True)
class SherpaTransducerFiles:
    tokens: Path
    encoder: Path
    decoder: Path
    joiner: Path


class SpeechRecognizer(Protocol):
    engine_id: str

    def recognize(self, audio: CapturedAudio) -> str: ...


def normalize_transcript(text: str) -> str:
    normalized = " ".join(text.split())
    if not normalized:
        raise SpeechNoMatchError("Could not understand audio.")
    return normalized


class GoogleSpeechRecognizer:
    engine_id = SPEECH_ENGINE_GOOGLE

    def __init__(self, recognizer: object, language: str) -> None:
        self._recognizer = recognizer
        self._language = language

    def recognize(self, audio: CapturedAudio) -> str:
        source_audio = sr.AudioData(
            audio.raw_data,
            audio.sample_rate_hz,
            audio.sample_width_bytes,
        )
        try:
            text = self._recognizer.recognize_google(source_audio, language=self._language)
        except sr.UnknownValueError as error:
            raise SpeechNoMatchError("Could not understand audio.") from error
        except sr.RequestError as error:
            raise SpeechServiceError("Google Speech request failed.") from error
        except Exception as error:
            raise SpeechServiceError("Google Speech recognition failed.") from error
        return normalize_transcript(text)


class SherpaOnnxRecognizer:
    engine_id = SPEECH_ENGINE_SHERPA_ONNX

    def __init__(
        self,
        config: SpeechEngineConfig,
        *,
        sherpa_module: ModuleType | object | None = None,
    ) -> None:
        self._model_files = _resolve_sherpa_model_files(config.model_dir)
        self._sherpa_module = sherpa_module or _import_sherpa_onnx()
        try:
            self._recognizer = self._sherpa_module.OfflineRecognizer.from_transducer(
                tokens=str(self._model_files.tokens),
                encoder=str(self._model_files.encoder),
                decoder=str(self._model_files.decoder),
                joiner=str(self._model_files.joiner),
                num_threads=1,
                sample_rate=16_000,
                feature_dim=80,
                decoding_method="greedy_search",
                provider="cpu",
            )
        except Exception as error:
            raise SpeechConfigurationError("Could not initialize the Sherpa-ONNX model.") from error

    def recognize(self, audio: CapturedAudio) -> str:
        if audio.sample_rate_hz != 16_000 or audio.sample_width_bytes != 2:
            raise SpeechConfigurationError("Sherpa-ONNX requires 16 kHz 16-bit mono PCM audio.")
        samples = np.frombuffer(audio.raw_data, dtype=np.int16).astype(np.float32) / 32768.0
        stream = self._recognizer.create_stream()
        try:
            stream.accept_waveform(audio.sample_rate_hz, samples)
            self._recognizer.decode_stream(stream)
            return normalize_transcript(stream.result.text)
        except SpeechRecognitionError:
            raise
        except Exception as error:
            raise SpeechServiceError("Sherpa-ONNX could not recognize this audio.") from error


def build_speech_recognizer(
    config: SpeechEngineConfig,
    *,
    google_recognizer: object | None = None,
) -> SpeechRecognizer:
    if config.engine_id == SPEECH_ENGINE_GOOGLE:
        return GoogleSpeechRecognizer(google_recognizer or sr.Recognizer(), config.language)
    if config.engine_id == SPEECH_ENGINE_SHERPA_ONNX:
        return SherpaOnnxRecognizer(config)
    raise SpeechConfigurationError("Select a valid speech input engine.")


def speech_settings_from_mapping(settings: Mapping[str, object]) -> SpeechEngineConfig:
    engine_id = settings.get("speech_engine")
    if engine_id not in (SPEECH_ENGINE_GOOGLE, SPEECH_ENGINE_SHERPA_ONNX):
        engine_id = SPEECH_ENGINE_GOOGLE
    model_dir_value = settings.get("speech_model_dir")
    model_dir = Path(model_dir_value) if isinstance(model_dir_value, str) and model_dir_value.strip() else None
    return SpeechEngineConfig(engine_id=engine_id, model_dir=model_dir)


def speech_settings_for_persistence(config: SpeechEngineConfig) -> dict[str, str]:
    return {
        "speech_engine": config.engine_id,
        "speech_model_dir": str(config.model_dir) if config.model_dir is not None else "",
    }


def _resolve_sherpa_model_files(model_dir: Path | None) -> SherpaTransducerFiles:
    if model_dir is None or not model_dir.is_dir():
        raise SpeechConfigurationError("Select a Sherpa-ONNX model folder.")
    tokens = model_dir / _SHERPA_TOKENS_FILE
    if not tokens.is_file():
        raise SpeechConfigurationError("Sherpa-ONNX model folder must contain tokens.txt.")
    try:
        return SherpaTransducerFiles(
            tokens=tokens,
            encoder=_select_sherpa_component(model_dir, "encoder"),
            decoder=_select_sherpa_component(model_dir, "decoder"),
            joiner=_select_sherpa_component(model_dir, "joiner"),
        )
    except LookupError as error:
        raise SpeechConfigurationError(
            "Sherpa-ONNX model folder must contain encoder*.onnx, decoder*.onnx, and joiner*.onnx."
        ) from error


def _select_sherpa_component(model_dir: Path, component: str) -> Path:
    matches = sorted(model_dir.glob(f"{component}*.onnx"))
    if not matches:
        raise LookupError(component)
    return min(matches, key=lambda path: (".int8." not in path.name, len(path.name), path.name))


def _import_sherpa_onnx() -> ModuleType:
    try:
        import sherpa_onnx
    except ImportError as error:
        raise SpeechEngineUnavailable(
            "Sherpa-ONNX is not installed. Install Ru with the speech-offline extra."
        ) from error
    return sherpa_onnx
