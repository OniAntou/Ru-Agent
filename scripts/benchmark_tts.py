from __future__ import annotations

import argparse
import contextlib
import ctypes
import importlib
import json
import os
import shutil
import subprocess
import sys
import threading
from collections.abc import Callable
from pathlib import Path
from time import perf_counter

from ru_runtime.voice.benchmark import (
    BenchmarkUtterance,
    ResourceSampler,
    ResourceUsage,
    load_manifest,
    run_engine_benchmark,
    summarize,
)
from ru_runtime.voice.omnivoice_tuning import (
    BASELINE_SETTINGS,
    CANDIDATE_SETTINGS,
    OmniVoiceGenerationSettings,
)

SETTINGS_BY_ID = {settings.config_id: settings for settings in CANDIDATE_SETTINGS}


class GwenHttpBenchmarkEngine:
    engine_id = "gwen"

    def __init__(
        self,
        *,
        http: object | None = None,
        base_url: str = "http://127.0.0.1:5051",
        voice_profile: str = "ru",
    ) -> None:
        self._http = http
        self._base_url = base_url.rstrip("/")
        self._voice_profile = voice_profile
        self.process_id: int | None = None

    def _get(self, path: str):
        if self._http is not None:
            return self._http.get(path)
        import requests

        return requests.get(f"{self._base_url}{path}", timeout=30)

    def _post(self, path: str, payload: dict[str, str] | None = None):
        if self._http is not None:
            return self._http.post(path, payload)
        import requests

        return requests.post(f"{self._base_url}{path}", json=payload or {}, timeout=120)

    @staticmethod
    def _json_response(response: object, operation: str) -> dict[str, object]:
        status_code = getattr(response, "status_code", None)
        if status_code != 200:
            raise RuntimeError(f"Gwen {operation} failed with HTTP {status_code}.")
        try:
            value = response.json()
        except Exception as error:
            raise RuntimeError(f"Gwen {operation} returned invalid JSON.") from error
        if not isinstance(value, dict):
            raise RuntimeError(f"Gwen {operation} returned an invalid payload.")
        if value.get("error"):
            raise RuntimeError(f"Gwen {operation} failed: {value['error']}")
        return value

    def load(self, reference_audio: Path, reference_text: str) -> None:
        del reference_audio, reference_text
        status = self._json_response(self._get("/status"), "status")
        pid = status.get("pid")
        if not isinstance(pid, int) or pid <= 0:
            raise RuntimeError("Gwen status did not provide a valid service PID.")
        self.process_id = pid
        self._json_response(self._post("/warmup", {"voice_profile": self._voice_profile}), "warmup")

    def synthesize(self, text: str, output_path: Path) -> None:
        response = self._json_response(
            self._post("/tts", {"text": text, "voice_profile": self._voice_profile}), "synthesis"
        )
        source_path = response.get("audio_path")
        if not isinstance(source_path, str) or not Path(source_path).is_file():
            raise RuntimeError("Gwen did not return a playable WAV path.")
        output_path.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source_path, output_path)
        if not output_path.is_file() or output_path.stat().st_size == 0:
            output_path.unlink(missing_ok=True)
            raise RuntimeError("Gwen returned an empty WAV file.")

    def release(self) -> None:
        try:
            self._json_response(self._post("/release"), "release")
        finally:
            self.process_id = None


class OmniVoiceBenchmarkEngine:
    engine_id = "omnivoice"

    def __init__(
        self,
        *,
        model_cache: Path,
        model_id: str = "k2-fsa/OmniVoice",
        settings: OmniVoiceGenerationSettings = BASELINE_SETTINGS,
        importer: Callable[[str], object] = importlib.import_module,
    ) -> None:
        self._model_cache = model_cache
        self._model_id = model_id
        self._settings = settings
        self._importer = importer
        self._model: object | None = None
        self._prompt: object | None = None
        self._torch: object | None = None
        self._soundfile: object | None = None
        self.process_id: int | None = None

    def load(self, reference_audio: Path, reference_text: str) -> None:
        if not reference_audio.is_file() or not reference_text.strip():
            raise RuntimeError("OmniVoice requires the fixed reference audio and matching text.")
        self._model_cache.mkdir(parents=True, exist_ok=True)
        os.environ["HF_HOME"] = str(self._model_cache)
        os.environ["HF_HUB_CACHE"] = str(self._model_cache / "hub")
        try:
            omnivoice_module = self._importer("omnivoice")
            torch = self._importer("torch")
            soundfile = self._importer("soundfile")
        except ImportError as error:
            raise RuntimeError("OmniVoice is unavailable in this environment.") from error
        if not torch.cuda.is_available():
            raise RuntimeError("OmniVoice requires CUDA for this benchmark.")
        self._model = omnivoice_module.OmniVoice.from_pretrained(
            self._model_id,
            device_map="cuda:0",
            dtype=torch.float16,
        )
        self._prompt = self._model.create_voice_clone_prompt(
            ref_audio=str(reference_audio), ref_text=reference_text
        )
        self._torch = torch
        self._soundfile = soundfile
        self.process_id = os.getpid()

    def synthesize(self, text: str, output_path: Path) -> None:
        if self._model is None or self._prompt is None or self._soundfile is None:
            raise RuntimeError("OmniVoice is not loaded.")
        with self._torch.inference_mode():
            audio = self._model.generate(
                text=text,
                voice_clone_prompt=self._prompt,
                **self._settings.generate_kwargs(),
            )
        if not audio:
            raise RuntimeError("OmniVoice returned no audio.")
        output_path.parent.mkdir(parents=True, exist_ok=True)
        self._soundfile.write(str(output_path), audio[0], 24_000)

    def release(self) -> None:
        self._prompt = None
        self._model = None
        if self._torch is not None and self._torch.cuda.is_available():
            self._torch.cuda.empty_cache()
        self._torch = None
        self._soundfile = None
        self.process_id = None

    def benchmark_metadata(self) -> dict[str, object]:
        return {
            "generation_config_id": self._settings.config_id,
            "generation_settings": self._settings.generate_kwargs(),
        }


def _windows_process_ram_mib(pid: int) -> float | None:
    if os.name != "nt":
        return None

    class ProcessMemoryCountersEx(ctypes.Structure):
        _fields_ = [
            ("cb", ctypes.c_ulong),
            ("PageFaultCount", ctypes.c_ulong),
            ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t),
            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
            ("PagefileUsage", ctypes.c_size_t),
            ("PeakPagefileUsage", ctypes.c_size_t),
            ("PrivateUsage", ctypes.c_size_t),
        ]

    process = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)
    if not process:
        return None
    try:
        counters = ProcessMemoryCountersEx()
        counters.cb = ctypes.sizeof(counters)
        if not ctypes.windll.psapi.GetProcessMemoryInfo(process, ctypes.byref(counters), counters.cb):
            return None
        return round(counters.PrivateUsage / (1024 * 1024), 3)
    finally:
        ctypes.windll.kernel32.CloseHandle(process)


def _nvidia_gpu_mib(pid: int) -> float | None:
    try:
        completed = subprocess.run(
            [
                "nvidia-smi",
                "--query-compute-apps=pid,used_memory",
                "--format=csv,noheader,nounits",
            ],
            capture_output=True,
            check=False,
            text=True,
            timeout=10,
        )
    except (OSError, subprocess.SubprocessError):
        return None
    if completed.returncode != 0:
        return None
    for line in completed.stdout.splitlines():
        parts = [part.strip() for part in line.split(",", maxsplit=1)]
        if len(parts) != 2:
            continue
        try:
            candidate_pid = int(parts[0])
            memory_mib = float(parts[1])
        except ValueError:
            continue
        if candidate_pid == pid:
            return memory_mib
    return None


class ProcessResourceSampler:
    def __init__(
        self,
        pid: int,
        *,
        memory_probe: Callable[[int], float | None] = _windows_process_ram_mib,
        gpu_probe: Callable[[int], float | None] = _nvidia_gpu_mib,
        poll_seconds: float = 0.1,
    ) -> None:
        self._pid = pid
        self._memory_probe = memory_probe
        self._gpu_probe = gpu_probe
        self._poll_seconds = poll_seconds

    def measure(self, operation):
        memory_samples: list[float] = []
        gpu_samples: list[float] = []

        def capture() -> None:
            try:
                memory = self._memory_probe(self._pid)
            except Exception:
                memory = None
            try:
                gpu = self._gpu_probe(self._pid)
            except Exception:
                gpu = None
            if isinstance(memory, (int, float)):
                memory_samples.append(float(memory))
            if isinstance(gpu, (int, float)):
                gpu_samples.append(float(gpu))

        stop = threading.Event()

        def poll() -> None:
            while not stop.wait(self._poll_seconds):
                capture()

        capture()
        worker = threading.Thread(target=poll, daemon=True)
        worker.start()
        try:
            value = operation()
        finally:
            stop.set()
            worker.join()
            capture()
        return value, ResourceUsage(
            process_ram_mib=max(memory_samples, default=None),
            gpu_peak_mib=max(gpu_samples, default=None),
        )


def run_benchmark(
    engine,
    utterances: list[BenchmarkUtterance],
    *,
    reference_audio: Path,
    reference_text: str,
    output_dir: Path,
    voice_profile: str = "ru",
    sampler: ResourceSampler | None = None,
    load_clock: Callable[[], float] = perf_counter,
    clock: Callable[[], float] = perf_counter,
    warm_trials: int = 5,
) -> dict[str, object]:
    metadata_provider = getattr(engine, "benchmark_metadata", None)
    engine_metadata = metadata_provider() if callable(metadata_provider) else {}
    if not isinstance(engine_metadata, dict):
        raise TypeError("Benchmark engine metadata must be a dictionary.")
    record_metadata = {**engine_metadata, "voice_profile": voice_profile}

    def with_metadata(records: list[dict[str, object]]) -> list[dict[str, object]]:
        return [{**record, **record_metadata} for record in records]

    load_started = load_clock()
    try:
        engine.load(reference_audio, reference_text)
    except Exception as error:
        model_load_seconds = max(0.0, load_clock() - load_started)
        with contextlib.suppress(Exception):
            engine.release()
        records = with_metadata([{
            "schema_version": 1,
            "engine_id": engine.engine_id,
            "phase": "load",
            "trial": None,
            "utterance_id": None,
            "status": "error",
            "error_type": type(error).__name__,
            "message": str(error) or type(error).__name__,
        }])
        return {
            "schema_version": 1,
            "engine_id": engine.engine_id,
            **record_metadata,
            "model_load_seconds": model_load_seconds,
            "records": records,
            "summary": summarize(records),
        }
    model_load_seconds = max(0.0, load_clock() - load_started)
    active_sampler = sampler
    if active_sampler is None:
        process_id = getattr(engine, "process_id", None)
        if isinstance(process_id, int) and process_id > 0:
            active_sampler = ProcessResourceSampler(process_id)
    records = with_metadata(run_engine_benchmark(
        engine,
        utterances,
        reference_audio=reference_audio,
        reference_text=reference_text,
        output_dir=output_dir,
        sampler=active_sampler,
        clock=clock,
        load_engine=False,
        warm_trials=warm_trials,
    ))
    return {
        "schema_version": 1,
        "engine_id": engine.engine_id,
        **record_metadata,
        "model_load_seconds": model_load_seconds,
        "records": records,
        "summary": summarize(records),
    }


def write_report(path: Path, report: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def build_engine(
    engine_id: str,
    model_cache: Path,
    omnivoice_settings: OmniVoiceGenerationSettings = BASELINE_SETTINGS,
    voice_profile: str = "ru",
):
    if engine_id == "gwen":
        return GwenHttpBenchmarkEngine(voice_profile=voice_profile)
    if engine_id == "omnivoice":
        return OmniVoiceBenchmarkEngine(model_cache=model_cache, settings=omnivoice_settings)
    raise ValueError(f"Unsupported benchmark engine: {engine_id}")


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    project_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description="Benchmark one Ru local TTS engine at a time.")
    parser.add_argument("--engine", choices=("gwen", "omnivoice"), required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--reference-audio", type=Path, default=project_root / "tyc_talk.wav")
    parser.add_argument("--reference-text", type=Path, default=project_root / "reference_text.txt")
    parser.add_argument("--model-cache", type=Path, default=project_root / "models" / "omnivoice")
    parser.add_argument("--warm-trials", type=int, default=5)
    parser.add_argument("--omnivoice-config", choices=tuple(SETTINGS_BY_ID), default=BASELINE_SETTINGS.config_id)
    parser.add_argument("--voice-profile", choices=("ru", "ai_vy"), default="ru")
    return parser.parse_args(argv)


def main(
    argv: list[str] | None = None,
    *,
    engine_factory: Callable[[str, Path, OmniVoiceGenerationSettings, str], object] = build_engine,
) -> int:
    try:
        return _run(argv, engine_factory=engine_factory)
    except Exception as error:
        print(f"Benchmark failed: {type(error).__name__}: {error}", file=sys.stderr)
        return 1


def _run(
    argv: list[str] | None,
    *,
    engine_factory: Callable[[str, Path, OmniVoiceGenerationSettings, str], object],
) -> int:
    args = parse_args(argv)
    if args.warm_trials < 0 or args.warm_trials > 20:
        raise SystemExit("--warm-trials must be between 0 and 20.")
    try:
        reference_text = args.reference_text.read_text(encoding="utf-8").strip()
    except OSError as error:
        raise SystemExit(f"Could not read reference text: {args.reference_text}") from error
    if not args.reference_audio.is_file() or not reference_text:
        raise SystemExit("Reference audio and matching non-empty reference text are required.")
    report = run_benchmark(
        engine_factory(
            args.engine,
            args.model_cache,
            SETTINGS_BY_ID[args.omnivoice_config],
            args.voice_profile,
        ),
        load_manifest(args.manifest),
        reference_audio=args.reference_audio,
        reference_text=reference_text,
        output_dir=args.output_dir,
        voice_profile=args.voice_profile,
        warm_trials=args.warm_trials,
    )
    write_report(args.report, report)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
