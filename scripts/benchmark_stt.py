from __future__ import annotations

import argparse
import json
import wave
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from pathlib import Path
from time import perf_counter

from ru_runtime.speech.runtime import (
    SPEECH_ENGINE_GOOGLE,
    CapturedAudio,
    SpeechConfigurationError,
    SpeechEngineConfig,
    SpeechRecognitionError,
    SpeechRecognizer,
    build_speech_recognizer,
)


@dataclass(frozen=True, slots=True)
class BenchmarkItem:
    audio_path: Path
    expected_text: str


def load_wav(path: Path) -> CapturedAudio:
    try:
        with wave.open(str(path), "rb") as input_wav:
            if input_wav.getcomptype() != "NONE":
                raise SpeechConfigurationError("Benchmark WAV files must use uncompressed PCM audio.")
            if input_wav.getnchannels() != 1:
                raise SpeechConfigurationError("Benchmark WAV files must be mono.")
            if input_wav.getsampwidth() != 2:
                raise SpeechConfigurationError("Benchmark WAV files must use 16-bit samples.")
            if input_wav.getframerate() != 16_000:
                raise SpeechConfigurationError("Benchmark WAV files must use a 16 kHz sample rate.")
            return CapturedAudio(input_wav.readframes(input_wav.getnframes()))
    except SpeechRecognitionError:
        raise
    except (OSError, wave.Error) as error:
        raise SpeechConfigurationError(f"Could not read benchmark WAV: {path}") from error


def run_benchmark(
    items: Iterable[BenchmarkItem],
    recognizer: SpeechRecognizer,
    *,
    load_audio: Callable[[Path], CapturedAudio] = load_wav,
    clock: Callable[[], float] = perf_counter,
) -> list[dict[str, object]]:
    records: list[dict[str, object]] = []
    for item in items:
        started = clock()
        try:
            transcript = recognizer.recognize(load_audio(item.audio_path))
        except SpeechRecognitionError as error:
            records.append(
                {
                    "engine_id": recognizer.engine_id,
                    "audio_path": str(item.audio_path),
                    "expected_text": item.expected_text,
                    "latency_ms": round((clock() - started) * 1000, 3),
                    "status": "error",
                    "error": str(error),
                }
            )
            continue
        except Exception as error:
            records.append(
                {
                    "engine_id": recognizer.engine_id,
                    "audio_path": str(item.audio_path),
                    "expected_text": item.expected_text,
                    "latency_ms": round((clock() - started) * 1000, 3),
                    "status": "error",
                    "error": f"{type(error).__name__}: {error}",
                }
            )
            continue
        records.append(
            {
                "engine_id": recognizer.engine_id,
                "audio_path": str(item.audio_path),
                "expected_text": item.expected_text,
                "transcript": transcript,
                "latency_ms": round((clock() - started) * 1000, 3),
                "status": "ok",
            }
        )
    return records


def load_manifest(path: Path) -> list[BenchmarkItem]:
    items: list[BenchmarkItem] = []
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError as error:
        raise SpeechConfigurationError(f"Could not read benchmark manifest: {path}") from error
    for line_number, line in enumerate(lines, start=1):
        if not line.strip():
            continue
        try:
            value = json.loads(line)
            audio_path = value["audio_path"]
            expected_text = value["expected_text"]
        except (KeyError, TypeError, json.JSONDecodeError) as error:
            raise SpeechConfigurationError(f"Invalid manifest item on line {line_number}.") from error
        if not isinstance(audio_path, str) or not isinstance(expected_text, str):
            raise SpeechConfigurationError(f"Invalid manifest item on line {line_number}.")
        candidate_path = Path(audio_path)
        items.append(
            BenchmarkItem(
                candidate_path if candidate_path.is_absolute() else path.parent / candidate_path,
                expected_text,
            )
        )
    return items


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Benchmark Ru speech recognition using supplied WAV files.")
    parser.add_argument("--engine", choices=(SPEECH_ENGINE_GOOGLE,), required=True)
    parser.add_argument("--model-dir", type=Path)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    recognizer = build_speech_recognizer(
        SpeechEngineConfig(engine_id=args.engine, model_dir=args.model_dir)
    )
    records = run_benchmark(load_manifest(args.manifest), recognizer)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8") as output_file:
        for record in records:
            output_file.write(json.dumps(record, ensure_ascii=False) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
