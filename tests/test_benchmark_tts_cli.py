from __future__ import annotations

import json
import os
import wave
from pathlib import Path
from typing import Any, cast

import pytest

from ru_runtime.voice.benchmark import BenchmarkUtterance
from ru_runtime.voice.omnivoice_tuning import TEXT_AWARE_20_SETTINGS
from scripts.benchmark_tts import (
    GwenHttpBenchmarkEngine,
    OmniVoiceBenchmarkEngine,
    ProcessResourceSampler,
    main,
    parse_args,
    run_benchmark,
    write_report,
)


class FakeResponse:
    def __init__(self, payload: dict[str, object], status_code: int = 200) -> None:
        self._payload = payload
        self.status_code = status_code
        self.text = str(payload)

    def json(self) -> dict[str, object]:
        return self._payload


class FakeGwenHttp:
    def __init__(self, source_wav: Path) -> None:
        self.source_wav = source_wav
        self.calls: list[tuple[str, str, dict[str, str] | None]] = []

    def get(self, path: str) -> FakeResponse:
        self.calls.append(("GET", path, None))
        return FakeResponse({"pid": 4321, "model_loaded": False})

    def post(self, path: str, payload: dict[str, str] | None = None) -> FakeResponse:
        self.calls.append(("POST", path, payload))
        if path == "/tts":
            return FakeResponse({"audio_path": str(self.source_wav)})
        return FakeResponse({"ready": True})


class ReportEngine:
    engine_id = "report"

    def load(self, reference_audio: Path, reference_text: str) -> None:
        assert reference_audio.is_file()
        assert reference_text == "reference text"

    def synthesize(self, text: str, output_path: Path) -> None:
        with wave.open(str(output_path), "wb") as output:
            output.setnchannels(1)
            output.setsampwidth(2)
            output.setframerate(16_000)
            output.writeframes(b"\0\0" * 16_000)

    def release(self) -> None:
        return None


class LoadFailureEngine:
    engine_id = "load-failure"

    def __init__(self) -> None:
        self.released = False

    def load(self, reference_audio: Path, reference_text: str) -> None:
        raise RuntimeError("CUDA out of memory")

    def release(self) -> None:
        self.released = True


class ConfiguredReportEngine(ReportEngine):
    def benchmark_metadata(self) -> dict[str, object]:
        return {
            "generation_config_id": "vi-normalized-20",
            "generation_settings": {
                "language": "Vietnamese",
                "normalize_text": True,
                "num_step": 20,
            },
        }


def test_gwen_engine_copies_returned_audio_and_releases_the_owned_service(tmp_path: Path) -> None:
    source = tmp_path / "source.wav"
    source.write_bytes(b"wav")
    http = FakeGwenHttp(source)
    engine = GwenHttpBenchmarkEngine(http=http)
    output = tmp_path / "output.wav"

    engine.load(tmp_path / "reference.wav", "reference text")
    engine.synthesize("Chào anh.", output)
    assert engine.process_id == 4321
    engine.release()

    assert output.read_bytes() == b"wav"
    assert engine.process_id is None
    assert http.calls == [
        ("GET", "/status", None),
        ("POST", "/warmup", {"voice_profile": "ru"}),
        ("POST", "/tts", {"text": "Chào anh.", "voice_profile": "ru"}),
        ("POST", "/release", None),
    ]


class InvalidPidHttp:
    def get(self, path: str) -> FakeResponse:
        return FakeResponse({"pid": 0, "model_loaded": False})

    def post(self, path: str, payload: dict[str, str] | None = None) -> FakeResponse:
        return FakeResponse({"ready": True})


def test_gwen_engine_rejects_a_status_without_a_valid_pid(tmp_path: Path) -> None:
    engine = GwenHttpBenchmarkEngine(http=InvalidPidHttp())

    with pytest.raises(RuntimeError, match="valid service PID"):
        engine.load(tmp_path / "reference.wav", "reference text")


def test_gwen_engine_rejects_a_missing_returned_audio_path(tmp_path: Path) -> None:
    missing = tmp_path / "missing.wav"
    http = FakeGwenHttp(missing)
    engine = GwenHttpBenchmarkEngine(http=http)

    engine.load(tmp_path / "reference.wav", "reference text")
    with pytest.raises(RuntimeError, match="playable WAV path"):
        engine.synthesize("Chào anh.", tmp_path / "output.wav")


def test_omnivoice_import_is_deferred_until_load(tmp_path: Path) -> None:
    import_calls: list[str] = []

    def missing_omnivoice(_: str):
        import_calls.append("called")
        raise ImportError("not installed")

    reference = tmp_path / "reference.wav"
    reference.write_bytes(b"reference")
    engine = OmniVoiceBenchmarkEngine(model_cache=tmp_path, importer=missing_omnivoice)

    assert engine.engine_id == "omnivoice"
    assert import_calls == []
    with pytest.raises(RuntimeError, match="OmniVoice is unavailable"):
        engine.load(reference, "reference text")
    assert import_calls == ["called"]


def test_omnivoice_load_exposes_its_own_process_for_resource_sampling(tmp_path: Path) -> None:
    reference = tmp_path / "reference.wav"
    reference.write_bytes(b"reference")

    class FakeCuda:
        @staticmethod
        def is_available() -> bool:
            return True

    class FakeTorch:
        cuda = FakeCuda()
        float16 = object()

    class FakeModel:
        def create_voice_clone_prompt(self, **kwargs):
            return kwargs

    class FakeOmniVoice:
        @staticmethod
        def from_pretrained(*args, **kwargs):
            return FakeModel()

    modules = {
        "omnivoice": type("OmniModule", (), {"OmniVoice": FakeOmniVoice}),
        "torch": FakeTorch(),
        "soundfile": object(),
    }
    engine = OmniVoiceBenchmarkEngine(model_cache=tmp_path / "cache", importer=modules.__getitem__)

    engine.load(reference, "reference text")

    assert engine.process_id == os.getpid()


def test_omnivoice_synthesis_passes_the_selected_text_handling_settings(tmp_path: Path) -> None:
    reference = tmp_path / "reference.wav"
    reference.write_bytes(b"reference")

    class FakeCuda:
        @staticmethod
        def is_available() -> bool:
            return True

    class FakeInferenceMode:
        def __enter__(self):
            return self

        def __exit__(self, exc_type, exc_value, traceback) -> None:
            return None

    class FakeTorch:
        cuda = FakeCuda()
        float16 = object()

        @staticmethod
        def inference_mode() -> FakeInferenceMode:
            return FakeInferenceMode()

    class FakeModel:
        def __init__(self) -> None:
            self.calls: list[dict[str, object]] = []

        def create_voice_clone_prompt(self, **kwargs):
            return {"prompt": kwargs}

        def generate(self, **kwargs):
            self.calls.append(kwargs)
            return [[0.0, 0.0]]

    model = FakeModel()

    class FakeOmniVoice:
        @staticmethod
        def from_pretrained(*args, **kwargs):
            return model

    class FakeSoundFile:
        @staticmethod
        def write(path: str, audio, sample_rate: int) -> None:
            Path(path).write_bytes(b"audio")

    modules = {
        "omnivoice": type("OmniModule", (), {"OmniVoice": FakeOmniVoice}),
        "torch": FakeTorch(),
        "soundfile": FakeSoundFile(),
    }
    engine = OmniVoiceBenchmarkEngine(
        model_cache=tmp_path / "cache",
        settings=TEXT_AWARE_20_SETTINGS,
        importer=modules.__getitem__,
    )

    engine.load(reference, "reference text")
    engine.synthesize("Xin chào anh.", tmp_path / "output.wav")

    assert model.calls == [{
        "text": "Xin chào anh.",
        "voice_clone_prompt": {"prompt": {"ref_audio": str(reference), "ref_text": "reference text"}},
        "language": "Vietnamese",
        "normalize_text": True,
        "num_step": 20,
    }]


def test_run_benchmark_adds_profile_and_engine_metadata_to_every_record(tmp_path: Path) -> None:
    reference = tmp_path / "reference.wav"
    reference.write_bytes(b"reference")

    report = cast(
        dict[str, Any],
        run_benchmark(
            ConfiguredReportEngine(),
            [BenchmarkUtterance("vi-01", "vi", "Chào anh.")],
            reference_audio=reference,
            reference_text="reference text",
            output_dir=tmp_path / "audio",
            voice_profile="ai_vy",
            clock=lambda: 1.0,
        ),
    )

    assert report["voice_profile"] == "ai_vy"
    assert report["generation_config_id"] == "vi-normalized-20"
    assert report["generation_settings"] == {
        "language": "Vietnamese",
        "normalize_text": True,
        "num_step": 20,
    }
    assert all(record["voice_profile"] == "ai_vy" for record in report["records"])
    assert all(record["generation_config_id"] == "vi-normalized-20" for record in report["records"])


def test_run_benchmark_records_privacy_safe_latency_fields(tmp_path: Path) -> None:
    reference = tmp_path / "reference.wav"
    reference.write_bytes(b"reference")

    report = cast(
        dict[str, Any],
        run_benchmark(
            ReportEngine(),
            [BenchmarkUtterance("vi-01", "vi", "Chào anh.")],
            reference_audio=reference,
            reference_text="reference text",
            output_dir=tmp_path / "audio",
            voice_profile="ru",
        ),
    )

    record = report["records"][0]
    assert {"first_audio_ms", "synthesis_ms", "audio_duration_ms"} <= record.keys()
    assert "text" not in record


def test_run_benchmark_writes_a_load_timed_json_report(tmp_path: Path) -> None:
    reference = tmp_path / "reference.wav"
    reference.write_bytes(b"reference")
    report = cast(
        dict[str, Any],
        run_benchmark(
            ReportEngine(),
            [BenchmarkUtterance("vi-01", "vi", "Chào anh.")],
            reference_audio=reference,
            reference_text="reference text",
            output_dir=tmp_path / "audio",
            load_clock=iter((5.0, 7.5)).__next__,
            clock=lambda: 1.0,
        ),
    )
    report_path = tmp_path / "report.json"

    write_report(report_path, report)

    assert report["model_load_seconds"] == 2.5
    assert report["summary"]["successful_records"] == 6
    assert json.loads(report_path.read_text(encoding="utf-8"))["engine_id"] == "report"


def test_run_benchmark_records_a_failed_model_load_without_fallback(tmp_path: Path) -> None:
    reference = tmp_path / "reference.wav"
    reference.write_bytes(b"reference")
    engine = LoadFailureEngine()

    report = run_benchmark(
        engine,
        [BenchmarkUtterance("vi-01", "vi", "Chào anh.")],
        reference_audio=reference,
        reference_text="reference text",
        output_dir=tmp_path / "audio",
        load_clock=iter((5.0, 6.0)).__next__,
    )

    assert engine.released is True
    assert report["model_load_seconds"] == 1.0
    assert report["records"] == [{
        "schema_version": 1,
        "engine_id": "load-failure",
        "phase": "load",
        "trial": None,
        "utterance_id": None,
        "status": "error",
        "error_type": "RuntimeError",
        "message": "CUDA out of memory",
        "voice_profile": "ru",
    }]


def test_process_sampler_keeps_the_highest_value_for_only_the_selected_pid() -> None:
    requested_pids: list[int] = []

    def memory_probe(pid: int) -> float:
        requested_pids.append(pid)
        return 256.0 if len(requested_pids) == 1 else 384.0

    sampler = ProcessResourceSampler(
        4321,
        memory_probe=memory_probe,
        gpu_probe=lambda pid: 1024.0 if pid == 4321 else None,
        poll_seconds=0.001,
    )

    value, usage = sampler.measure(lambda: "audio")

    assert value == "audio"
    assert requested_pids and set(requested_pids) == {4321}
    assert usage.process_ram_mib == 384.0
    assert usage.gpu_peak_mib == 1024.0


def test_cli_resolves_only_known_omnivoice_settings_and_voice_profiles() -> None:
    common = [
        "--engine", "omnivoice", "--manifest", "manifest.jsonl", "--output-dir", "audio",
        "--report", "report.json",
    ]

    args = parse_args([
        *common,
        "--omnivoice-config", "vi-normalized-24",
        "--voice-profile", "ai_vy",
    ])

    assert args.omnivoice_config == "vi-normalized-24"
    assert args.voice_profile == "ai_vy"
    assert parse_args(common).omnivoice_config == "baseline-16"
    assert parse_args(common).voice_profile == "ru"
    with pytest.raises(SystemExit):
        parse_args([*common, "--omnivoice-config", "not-a-config"])
    with pytest.raises(SystemExit):
        parse_args([*common, "--voice-profile", "not-a-profile"])


def test_main_runs_a_selected_engine_and_writes_its_report(tmp_path: Path) -> None:
    manifest = tmp_path / "manifest.jsonl"
    manifest.write_text(
        "".join(
            json.dumps({"id": item_id, "language": language, "text": text}) + "\n"
            for item_id, language, text in [
                *((f"vi-{index:02}", "vi", f"Câu {index}") for index in range(1, 7)),
                *((f"en-{index:02}", "en", f"English {index}") for index in range(1, 4)),
                *((f"mix-{index:02}", "vi-en", f"Mixed {index}") for index in range(1, 4)),
            ]
        ),
        encoding="utf-8",
    )
    reference = tmp_path / "reference.wav"
    reference.write_bytes(b"reference")
    reference_text = tmp_path / "reference.txt"
    reference_text.write_text("reference text", encoding="utf-8")
    report_path = tmp_path / "report.json"

    exit_code = main(
        [
            "--engine", "gwen", "--manifest", str(manifest), "--output-dir", str(tmp_path / "audio"),
            "--report", str(report_path), "--reference-audio", str(reference),
            "--reference-text", str(reference_text),
        ],
        engine_factory=lambda engine_id, model_cache, settings, voice_profile: ReportEngine(),
    )

    assert exit_code == 0
    assert json.loads(report_path.read_text(encoding="utf-8"))["summary"]["successful_records"] == 72
