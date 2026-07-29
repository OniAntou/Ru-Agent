from __future__ import annotations

from pathlib import Path
import json
import os
import wave

import pytest

from mini_gpt.tts_benchmark import BenchmarkUtterance
from scripts.benchmark_tts import (
    GwenHttpBenchmarkEngine,
    OmniVoiceBenchmarkEngine,
    ProcessResourceSampler,
    main,
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


def test_run_benchmark_writes_a_load_timed_json_report(tmp_path: Path) -> None:
    reference = tmp_path / "reference.wav"
    reference.write_bytes(b"reference")
    report = run_benchmark(
        ReportEngine(),
        [BenchmarkUtterance("vi-01", "vi", "Chào anh.")],
        reference_audio=reference,
        reference_text="reference text",
        output_dir=tmp_path / "audio",
        load_clock=iter((5.0, 7.5)).__next__,
        clock=lambda: 1.0,
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
        engine_factory=lambda engine_id, model_cache: ReportEngine(),
    )

    assert exit_code == 0
    assert json.loads(report_path.read_text(encoding="utf-8"))["summary"]["successful_records"] == 72
