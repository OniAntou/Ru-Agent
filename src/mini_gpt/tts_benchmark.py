from __future__ import annotations

import json
import logging
import re
import statistics
import wave
from collections.abc import Callable, Iterable, Sequence
from dataclasses import dataclass
from pathlib import Path
from time import perf_counter
from typing import Protocol, TypeVar


logger = logging.getLogger(__name__)

_SAFE_ID_PATTERN = re.compile(r"^[A-Za-z0-9_.-]+$")
_ID_SANITIZE_PATTERN = re.compile(r"[^A-Za-z0-9_.-]+")


class BenchmarkConfigurationError(ValueError):
    """Raised when a benchmark input cannot produce comparable results."""


@dataclass(frozen=True, slots=True)
class BenchmarkUtterance:
    id: str
    language: str
    text: str


@dataclass(frozen=True, slots=True)
class ResourceUsage:
    process_ram_mib: float | None = None
    gpu_peak_mib: float | None = None


class TtsBenchmarkEngine(Protocol):
    engine_id: str

    def load(self, reference_audio: Path, reference_text: str) -> None: ...

    def synthesize(self, text: str, output_path: Path) -> None: ...

    def release(self) -> None: ...


ResultT = TypeVar("ResultT")


class ResourceSampler(Protocol):
    def measure(self, operation: Callable[[], ResultT]) -> tuple[ResultT, ResourceUsage]: ...


class NullResourceSampler:
    def measure(self, operation: Callable[[], ResultT]) -> tuple[ResultT, ResourceUsage]:
        return operation(), ResourceUsage()


_EXPECTED_LANGUAGE_COUNTS = {"vi": 6, "en": 3, "vi-en": 3}


def load_manifest(path: Path) -> list[BenchmarkUtterance]:
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError as error:
        raise BenchmarkConfigurationError(f"Could not read benchmark manifest: {path}") from error

    utterances: list[BenchmarkUtterance] = []
    seen_ids: set[str] = set()
    for line_number, line in enumerate(lines, start=1):
        if not line.strip():
            raise BenchmarkConfigurationError(f"Blank benchmark manifest line: {line_number}.")
        try:
            value = json.loads(line)
            utterance_id = value["id"]
            language = value["language"]
            text = value["text"]
        except (KeyError, TypeError, json.JSONDecodeError) as error:
            raise BenchmarkConfigurationError(f"Invalid benchmark manifest line: {line_number}.") from error
        if not all(isinstance(item, str) for item in (utterance_id, language, text)):
            raise BenchmarkConfigurationError(f"Invalid benchmark manifest line: {line_number}.")
        if not _SAFE_ID_PATTERN.fullmatch(utterance_id):
            raise BenchmarkConfigurationError(
                f"Benchmark utterance IDs must match {_SAFE_ID_PATTERN.pattern!r}: {utterance_id!r}."
            )
        if not utterance_id.strip() or utterance_id in seen_ids:
            raise BenchmarkConfigurationError(f"Benchmark utterance IDs must be unique: {utterance_id!r}.")
        if language not in _EXPECTED_LANGUAGE_COUNTS:
            raise BenchmarkConfigurationError(f"Unsupported benchmark language: {language!r}.")
        if not text.strip():
            raise BenchmarkConfigurationError(f"Benchmark text must not be blank: {utterance_id}.")
        seen_ids.add(utterance_id)
        utterances.append(BenchmarkUtterance(utterance_id, language, text))

    expected_total = sum(_EXPECTED_LANGUAGE_COUNTS.values())
    language_counts = {language: sum(item.language == language for item in utterances) for language in _EXPECTED_LANGUAGE_COUNTS}
    if len(utterances) != expected_total or language_counts != _EXPECTED_LANGUAGE_COUNTS:
        raise BenchmarkConfigurationError(
            "Benchmark manifest must contain exactly "
            f"{expected_total} items: six vi, three en, and three vi-en."
        )
    return utterances


def inspect_wav(path: Path) -> float:
    try:
        with wave.open(str(path), "rb") as input_wav:
            frame_count = input_wav.getnframes()
            sample_rate = input_wav.getframerate()
    except (OSError, wave.Error) as error:
        raise BenchmarkConfigurationError(f"Could not inspect benchmark WAV: {path}") from error
    if frame_count <= 0 or sample_rate <= 0:
        raise BenchmarkConfigurationError(f"Benchmark WAV has no playable duration: {path}")
    return frame_count / sample_rate


def real_time_factor(synthesis_seconds: float, audio_seconds: float) -> float:
    if synthesis_seconds < 0 or audio_seconds <= 0:
        raise BenchmarkConfigurationError("Real-time factor requires non-negative synthesis and positive audio duration.")
    return synthesis_seconds / audio_seconds


def run_single_sample(
    engine: TtsBenchmarkEngine,
    utterance: BenchmarkUtterance,
    *,
    phase: str,
    trial: int,
    output_dir: Path,
    sampler: ResourceSampler | None = None,
    clock: Callable[[], float] = perf_counter,
) -> dict[str, object]:
    output_path = output_dir / f"{phase}-{trial:02d}-{_ID_SANITIZE_PATTERN.sub('_', utterance.id)}.wav"
    output_dir.mkdir(parents=True, exist_ok=True)
    active_sampler = sampler or NullResourceSampler()
    started = clock()
    try:
        _, usage = active_sampler.measure(lambda: engine.synthesize(utterance.text, output_path))
        elapsed = max(0.0, clock() - started)
        audio_seconds = inspect_wav(output_path)
    except Exception as error:
        logger.exception(
            "TTS benchmark synthesis failed: phase=%s trial=%s utterance=%s",
            phase,
            trial,
            utterance.id,
        )
        try:
            Path(output_path).unlink(missing_ok=True)
        except OSError:
            pass
        return {
            "schema_version": 1,
            "engine_id": engine.engine_id,
            "phase": phase,
            "trial": trial,
            "utterance_id": utterance.id,
            "language": utterance.language,
            "text": utterance.text,
            "output_path": str(output_path),
            "status": "error",
            "error_type": type(error).__name__,
            "message": str(error) or type(error).__name__,
        }
    return {
        "schema_version": 1,
        "engine_id": engine.engine_id,
        "phase": phase,
        "trial": trial,
        "utterance_id": utterance.id,
        "language": utterance.language,
        "text": utterance.text,
        "output_path": str(output_path),
        "first_playable_wav_seconds": elapsed,
        "total_synthesis_seconds": elapsed,
        "audio_seconds": audio_seconds,
        "real_time_factor": real_time_factor(elapsed, audio_seconds),
        "process_ram_mib": usage.process_ram_mib,
        "gpu_peak_mib": usage.gpu_peak_mib,
        "status": "ok",
    }


def run_engine_benchmark(
    engine: TtsBenchmarkEngine,
    utterances: Iterable[BenchmarkUtterance],
    *,
    reference_audio: Path,
    reference_text: str,
    output_dir: Path,
    sampler: ResourceSampler | None = None,
    clock: Callable[[], float] = perf_counter,
    load_engine: bool = True,
    warm_trials: int = 5,
    release_engine: bool = True,
) -> list[dict[str, object]]:
    """Run a benchmark, loading the engine first when requested.

    release_engine=False keeps the engine loaded for callers that manage
    the engine lifecycle themselves (e.g. report runners).
    """
    if warm_trials < 0:
        raise BenchmarkConfigurationError("Warm trial count must not be negative.")
    records: list[dict[str, object]] = []
    try:
        if load_engine:
            engine.load(reference_audio, reference_text)
        for phase, trial in [("cold", 0), *(("warm", number) for number in range(1, warm_trials + 1))]:
            for utterance in utterances:
                records.append(
                    run_single_sample(
                        engine,
                        utterance,
                        phase=phase,
                        trial=trial,
                        output_dir=output_dir,
                        sampler=sampler,
                        clock=clock,
                    )
                )
    finally:
        if release_engine:
            engine.release()
    return records


def summarize(records: Sequence[dict[str, object]]) -> dict[str, object]:
    successful_records = [record for record in records if record.get("status") == "ok"]
    warm_records = [record for record in successful_records if record.get("phase") == "warm"]

    def median(field: str) -> float | None:
        values = [
            float(record[field])
            for record in warm_records
            if isinstance(record.get(field), (int, float)) and not isinstance(record.get(field), bool)
        ]
        return statistics.median(values) if values else None

    return {
        "records": len(records),
        "successful_records": len(successful_records),
        "failed_records": len(records) - len(successful_records),
        "median_warm_first_playable_wav_seconds": median("first_playable_wav_seconds"),
        "median_warm_real_time_factor": median("real_time_factor"),
    }
