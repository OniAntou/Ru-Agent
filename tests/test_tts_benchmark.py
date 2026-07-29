from __future__ import annotations

import json
import wave
from pathlib import Path

import pytest

from mini_gpt.tts_benchmark import (
    BenchmarkConfigurationError,
    BenchmarkUtterance,
    ResourceUsage,
    load_manifest,
    run_engine_benchmark,
    run_single_sample,
    summarize,
)


def _manifest_items() -> list[dict[str, str]]:
    return [
        *[
            {"id": f"vi-{index:02}", "language": "vi", "text": f"Câu Việt {index}."}
            for index in range(1, 7)
        ],
        *[
            {"id": f"en-{index:02}", "language": "en", "text": f"English {index}."}
            for index in range(1, 4)
        ],
        *[
            {"id": f"mix-{index:02}", "language": "vi-en", "text": f"Câu mixed {index}."}
            for index in range(1, 4)
        ],
    ]


def _write_manifest(path: Path, items: list[dict[str, str]]) -> None:
    path.write_text(
        "".join(json.dumps(item, ensure_ascii=False) + "\n" for item in items),
        encoding="utf-8",
    )


def _write_wav(path: Path, *, frames: int = 32_000, sample_rate: int = 16_000) -> None:
    with wave.open(str(path), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(sample_rate)
        output.writeframes(b"\0\0" * frames)


class StaticSampler:
    def measure(self, operation):
        return operation(), ResourceUsage(process_ram_mib=512.0, gpu_peak_mib=1024.0)


class WavEngine:
    engine_id = "fake"

    def __init__(self) -> None:
        self.calls: list[str] = []

    def load(self, reference_audio: Path, reference_text: str) -> None:
        assert reference_audio.name == "reference.wav"
        assert reference_text == "reference text"
        self.calls.append("load")

    def synthesize(self, text: str, output_path: Path) -> None:
        assert text
        self.calls.append("synthesize")
        _write_wav(output_path)

    def release(self) -> None:
        self.calls.append("release")


class FailingEngine:
    engine_id = "failing"

    def __init__(self) -> None:
        self.calls: list[str] = []

    def synthesize(self, text: str, output_path: Path) -> None:
        self.calls.append("synthesize")
        raise RuntimeError("CUDA out of memory")


def test_load_manifest_rejects_a_wrong_language_mix(tmp_path: Path) -> None:
    manifest = tmp_path / "manifest.jsonl"
    items = _manifest_items()
    items[-1]["language"] = "en"
    _write_manifest(manifest, items)

    with pytest.raises(BenchmarkConfigurationError, match="six vi, three en, and three vi-en"):
        load_manifest(manifest)


def test_committed_manifest_has_the_required_bilingual_mix() -> None:
    manifest = Path(__file__).parents[1] / "data" / "evaluations" / "ru_tts_benchmark_v1.jsonl"

    utterances = load_manifest(manifest)

    assert [(item.id, item.language) for item in utterances] == [
        *( (f"vi-{index:02}", "vi") for index in range(1, 7) ),
        *( (f"en-{index:02}", "en") for index in range(1, 4) ),
        *( (f"mix-{index:02}", "vi-en") for index in range(1, 4) ),
    ]


def test_run_single_sample_reports_first_playable_wav_and_rtf(tmp_path: Path) -> None:
    record = run_single_sample(
        WavEngine(),
        BenchmarkUtterance("vi-01", "vi", "Chào anh."),
        phase="warm",
        trial=1,
        output_dir=tmp_path,
        sampler=StaticSampler(),
        clock=iter((10.0, 10.5)).__next__,
    )

    assert record == {
        "schema_version": 1,
        "engine_id": "fake",
        "phase": "warm",
        "trial": 1,
        "utterance_id": "vi-01",
        "language": "vi",
        "text": "Chào anh.",
        "output_path": str(tmp_path / "warm-01-vi-01.wav"),
        "first_playable_wav_seconds": 0.5,
        "total_synthesis_seconds": 0.5,
        "audio_seconds": 2.0,
        "real_time_factor": 0.25,
        "process_ram_mib": 512.0,
        "gpu_peak_mib": 1024.0,
        "status": "ok",
    }


def test_failed_engine_record_does_not_call_a_fallback(tmp_path: Path) -> None:
    engine = FailingEngine()

    record = run_single_sample(
        engine,
        BenchmarkUtterance("vi-01", "vi", "Chào anh."),
        phase="cold",
        trial=0,
        output_dir=tmp_path,
    )

    assert engine.calls == ["synthesize"]
    assert record["status"] == "error"
    assert record["error_type"] == "RuntimeError"
    assert "audio_seconds" not in record


def test_run_engine_benchmark_runs_one_cold_and_five_warm_trials(tmp_path: Path) -> None:
    reference = tmp_path / "reference.wav"
    _write_wav(reference)
    engine = WavEngine()

    records = run_engine_benchmark(
        engine,
        [BenchmarkUtterance("vi-01", "vi", "Chào anh.")],
        reference_audio=reference,
        reference_text="reference text",
        output_dir=tmp_path / "output",
        sampler=StaticSampler(),
        clock=lambda: 1.0,
    )

    assert [(record["phase"], record["trial"]) for record in records] == [
        ("cold", 0),
        ("warm", 1),
        ("warm", 2),
        ("warm", 3),
        ("warm", 4),
        ("warm", 5),
    ]
    assert engine.calls == ["load", *("synthesize" for _ in range(6)), "release"]


def test_summarize_uses_median_warm_metrics_and_ignores_errors() -> None:
    summary = summarize(
        [
            {"status": "ok", "phase": "warm", "first_playable_wav_seconds": 0.2, "real_time_factor": 0.1},
            {"status": "ok", "phase": "warm", "first_playable_wav_seconds": 0.4, "real_time_factor": 0.3},
            {"status": "ok", "phase": "warm", "first_playable_wav_seconds": 0.6, "real_time_factor": 0.5},
            {"status": "error", "phase": "warm", "error_type": "RuntimeError"},
        ]
    )

    assert summary == {
        "records": 4,
        "successful_records": 3,
        "failed_records": 1,
        "median_warm_first_playable_wav_seconds": 0.4,
        "median_warm_real_time_factor": 0.3,
    }
