# Ru Offline STT Proof-of-Concept Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Add a selectable Sherpa-ONNX offline speech recognizer, keep Google as the unchanged default fallback, and supply a local WAV benchmark without automatic model downloads.

**Architecture:** A new speech_runtime module owns audio-neutral data, configuration validation, recognizer adapters, and the factory registry. Tkinter continues to capture microphone audio and converts it once into the neutral form before calling the selected adapter. The benchmark calls the same factory and never touches the GUI.

**Tech Stack:** Python 3.11+, SpeechRecognition, optional sherpa-onnx, Tkinter, PyAudio, pytest, standard-library wave and json.

## Global Constraints

- Preserve Google (vi-VN) as the default recognizer and do not silently fall back from Sherpa to Google.
- The Sherpa proof of concept is CPU-first; do not alter LLM/Gwen GPU lifecycle or start any background audio process.
- Do not download models automatically. A compatible user-supplied model directory must contain tokens.txt plus matching encoder*.onnx, decoder*.onnx, and joiner*.onnx files.
- Persist only speech_engine and speech_model_dir; never persist microphone audio, transcript text, or additional secrets.
- Keep VTS metadata, memory extraction, and TTS out of the recognition path.
- Add only new targeted tests; do not restore deleted legacy tests.

---

## File structure

- Create src/mini_gpt/speech_runtime.py: audio value object, error hierarchy, Google/Sherpa adapters, validation, and registry factory.
- Create tests/test_speech_runtime.py: contract tests for every adapter and error mapping.
- Modify src/mini_gpt/gui.py: selected-engine variables, persistence, a compact speech settings section, and adapter use after microphone capture.
- Create tests/test_gui_speech_settings.py: isolated settings serialization/default tests without creating a real Tk window or accessing a microphone.
- Create scripts/benchmark_stt.py: explicit-manifest WAV benchmark using the production factory.
- Create tests/test_benchmark_stt.py: benchmark records, timing, and no-download contract.
- Modify pyproject.toml: add an optional speech-offline dependency extra only.
- Modify README.md: document opt-in installation, accepted model layout, privacy behavior, benchmark command, and manual adoption gate.

## Task 1: Create the recognizer boundary and adapters

**Files:**
- Create: src/mini_gpt/speech_runtime.py
- Test: tests/test_speech_runtime.py

**Interfaces:**
- Produces CapturedAudio(raw_data: bytes, sample_rate_hz: int = 16000, sample_width_bytes: int = 2).
- Produces SpeechEngineConfig(engine_id: str = "google", model_dir: Path | None = None, language: str = "vi-VN").
- Produces SpeechRecognitionError, SpeechEngineUnavailable, SpeechConfigurationError, SpeechNoMatchError, and SpeechServiceError.
- Produces build_speech_recognizer(config: SpeechEngineConfig, *, google_recognizer: object | None = None) -> SpeechRecognizer and normalize_transcript(text: str) -> str.
- Consumes CapturedAudio and returns a non-empty normalized string through SpeechRecognizer.recognize(audio: CapturedAudio) -> str.

- [x] **Step 1: Write the failing test**

~~~python
from pathlib import Path

import pytest

from mini_gpt.speech_runtime import (
    CapturedAudio,
    SpeechConfigurationError,
    SpeechEngineConfig,
    SpeechEngineUnavailable,
    SpeechNoMatchError,
    build_speech_recognizer,
    normalize_transcript,
)


class FakeGoogleRecognizer:
    def recognize_google(self, _audio, *, language: str) -> str:
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


def test_sherpa_missing_optional_package_has_a_recovery_message(tmp_path: Path) -> None:
    for name in ("tokens.txt", "encoder.onnx", "decoder.onnx", "joiner.onnx"):
        (tmp_path / name).write_bytes(b"model")

    with pytest.raises(SpeechEngineUnavailable, match="speech-offline"):
        build_speech_recognizer(
            SpeechEngineConfig(engine_id="sherpa-onnx", model_dir=tmp_path)
        )


def test_empty_recognition_is_a_no_match_error() -> None:
    assert normalize_transcript("  Ru\n\t") == "Ru"
    with pytest.raises(SpeechNoMatchError):
        normalize_transcript(" \n ")
~~~

- [x] **Step 2: Run test to verify it fails**

Run: & .\.venv\Scripts\python.exe -m pytest tests\test_speech_runtime.py -q

Expected: FAIL with ModuleNotFoundError for mini_gpt.speech_runtime.

- [x] **Step 3: Write the minimal implementation**

~~~python
@dataclass(frozen=True, slots=True)
class CapturedAudio:
    raw_data: bytes
    sample_rate_hz: int = 16_000
    sample_width_bytes: int = 2


def normalize_transcript(text: str) -> str:
    normalized = " ".join(text.split())
    if not normalized:
        raise SpeechNoMatchError("Could not understand audio.")
    return normalized


def _validate_sherpa_model_dir(model_dir: Path | None) -> Path:
    if model_dir is None or not model_dir.is_dir():
        raise SpeechConfigurationError("Select a Sherpa-ONNX model folder.")
    missing = [name for name in _SHERPA_REQUIRED_FILES if not (model_dir / name).is_file()]
    if missing:
        raise SpeechConfigurationError(
            "Sherpa-ONNX model folder must contain " + ", ".join(_SHERPA_REQUIRED_FILES) + "."
        )
    return model_dir
~~~

Implement GoogleSpeechRecognizer by rebuilding speech_recognition.AudioData
from CapturedAudio and mapping UnknownValueError to SpeechNoMatchError and
RequestError to SpeechServiceError("Google Speech request failed.").
Implement SherpaOnnxRecognizer with a lazy import sherpa_onnx,
OfflineRecognizer.from_transducer(tokens=..., encoder=..., decoder=...,
joiner=..., provider="cpu", sample_rate=16000, feature_dim=80), then
create_stream, accept_waveform, decode_stream, and get_result. Map a missing
import to SpeechEngineUnavailable("Sherpa-ONNX is not installed. Install Ru with the speech-offline extra.").

- [x] **Step 4: Run test to verify it passes**

Run: & .\.venv\Scripts\python.exe -m pytest tests\test_speech_runtime.py -q

Expected: PASS; no optional Sherpa package or network request is required.

- [x] **Step 5: Commit**

~~~powershell
git add -- src/mini_gpt/speech_runtime.py tests/test_speech_runtime.py
git commit -m "feat: add selectable speech recognizer runtime"
~~~

## Task 2: Persist and use the selected recognizer in the GUI

**Files:**
- Modify: src/mini_gpt/gui.py:16, 350-410, 570-630, 816-834
- Create: tests/test_gui_speech_settings.py
- Test: tests/test_speech_runtime.py

**Interfaces:**
- Consumes SpeechEngineConfig, CapturedAudio, build_speech_recognizer, and the Task 1 error hierarchy.
- Produces speech_settings_from_mapping(settings: Mapping[str, object]) -> SpeechEngineConfig and speech_settings_for_persistence(config: SpeechEngineConfig) -> dict[str, str] in speech_runtime.py.
- Produces GUI variables speech_engine_var and speech_model_dir_var, whose persisted keys are exactly speech_engine and speech_model_dir.

- [x] **Step 1: Write the failing test**

~~~python
from mini_gpt.speech_runtime import (
    SpeechEngineConfig,
    speech_settings_for_persistence,
    speech_settings_from_mapping,
)


def test_gui_speech_settings_default_to_google_without_storing_transcript_or_key() -> None:
    default = speech_settings_from_mapping({"api_key": "secret", "transcript": "hello"})

    assert default == SpeechEngineConfig()
    assert speech_settings_for_persistence(default) == {
        "speech_engine": "google",
        "speech_model_dir": "",
    }


def test_gui_speech_settings_round_trip_the_explicit_offline_directory() -> None:
    selected = speech_settings_from_mapping(
        {"speech_engine": "sherpa-onnx", "speech_model_dir": "D:/LLM/models/sherpa"}
    )

    assert selected.engine_id == "sherpa-onnx"
    assert selected.model_dir is not None
    assert str(selected.model_dir).endswith("models\\sherpa")
~~~

- [x] **Step 2: Run test to verify it fails**

Run: & .\.venv\Scripts\python.exe -m pytest tests\test_gui_speech_settings.py -q

Expected: FAIL with an import error for speech_settings_for_persistence.

- [x] **Step 3: Write the minimal implementation**

Add settings serializer/parser functions in speech_runtime.py. In RuApp.__init__,
create tk.StringVar values from them and extend _save_settings with only the two
returned keys. Trace both variables to _save_settings.

In _open_settings, add a Speech input ttk.LabelFrame before the privacy section,
containing a readonly combobox with labels Google (online) and Sherpa-ONNX
(offline), a model-folder entry, and a Browse button using
filedialog.askdirectory.

Replace direct recognize_google in _record_worker with:

~~~python
raw = audio.get_raw_data(convert_rate=16_000, convert_width=2)
captured = CapturedAudio(raw)
runtime = build_speech_recognizer(
    self._speech_config(), google_recognizer=self.microphone_recognizer
)
text = runtime.recognize(captured)
~~~

Rename the existing self.recognizer field to self.microphone_recognizer.
Catch only Task 1 typed recognition errors and pass their message to
_on_record_error; retain the existing capture timeout branch. Do not call
Google when the chosen Sherpa adapter fails.

- [x] **Step 4: Run test to verify it passes**

Run: & .\.venv\Scripts\python.exe -m pytest tests\test_speech_runtime.py tests\test_gui_speech_settings.py -q

Expected: PASS. Inspect data/gui_settings.json only through a temporary test
path; no real Tk window, microphone, key, or transcript is used.

- [x] **Step 5: Commit**

~~~powershell
git add -- src/mini_gpt/speech_runtime.py src/mini_gpt/gui.py tests/test_gui_speech_settings.py tests/test_speech_runtime.py
git commit -m "feat: configure offline speech input in Ru GUI"
~~~

## Task 3: Add the explicit WAV benchmark and offline dependency extra

**Files:**
- Modify: pyproject.toml:21-33
- Create: scripts/benchmark_stt.py
- Create: tests/test_benchmark_stt.py

**Interfaces:**
- Consumes SpeechEngineConfig, CapturedAudio, and build_speech_recognizer from Task 1.
- Produces BenchmarkItem(audio_path: Path, expected_text: str) and run_benchmark(items, recognizer, *, clock=perf_counter) -> list[dict[str, object]].
- Emits JSONL records with exactly engine_id, audio_path, expected_text, transcript, latency_ms, status, and optional error.

- [x] **Step 1: Write the failing test**

~~~python
from pathlib import Path

from scripts.benchmark_stt import BenchmarkItem, run_benchmark
from mini_gpt.speech_runtime import CapturedAudio


class FakeRecognizer:
    engine_id = "fake"

    def recognize(self, audio: CapturedAudio) -> str:
        assert audio.raw_data == b"pcm"
        return "xin chao"


def test_benchmark_records_transcript_latency_and_expected_text(tmp_path: Path) -> None:
    wav_path = tmp_path / "sample.wav"
    wav_path.write_bytes(b"not used by this unit seam")
    item = BenchmarkItem(wav_path, "xin chao")

    records = run_benchmark(
        [item], FakeRecognizer(), load_audio=lambda _path: CapturedAudio(b"pcm"),
        clock=iter((1.0, 1.125)).__next__,
    )

    assert records == [{
        "engine_id": "fake", "audio_path": str(wav_path), "expected_text": "xin chao",
        "transcript": "xin chao", "latency_ms": 125.0, "status": "ok",
    }]
~~~

- [x] **Step 2: Run test to verify it fails**

Run: & .\.venv\Scripts\python.exe -m pytest tests\test_benchmark_stt.py -q

Expected: FAIL with ModuleNotFoundError for scripts.benchmark_stt.

- [x] **Step 3: Write the minimal implementation**

Add speech-offline = ["sherpa-onnx>=1.12,<2"] under optional dependencies.
Implement a standard-library WAV loader that accepts only uncompressed PCM,
mono, 16-bit, 16 kHz input and converts it to CapturedAudio. run_benchmark
catches SpeechRecognitionError and emits a
status: "error" record without a transcript; it never retries through another
engine. The argparse CLI accepts --engine, --model-dir, --manifest, and
--output, reads JSONL manifest items, and writes JSONL records only to the
explicit --output path.

- [x] **Step 4: Run test to verify it passes**

Run: & .\.venv\Scripts\python.exe -m pytest tests\test_benchmark_stt.py -q

Expected: PASS. Confirm the test uses only its temporary path and never imports
or downloads Sherpa-ONNX.

- [x] **Step 5: Commit**

~~~powershell
git add -- pyproject.toml scripts/benchmark_stt.py tests/test_benchmark_stt.py
git commit -m "feat: add offline speech benchmark"
~~~

## Task 4: Document, run regression checks, and perform the hardware gate

**Files:**
- Modify: README.md:216-243 or a new nearby Voice input section
- Test: tests/test_speech_runtime.py
- Test: tests/test_gui_speech_settings.py
- Test: tests/test_benchmark_stt.py

**Interfaces:**
- Consumes the install extra, model layout, GUI settings, and CLI defined by Tasks 1-3.
- Produces a reproducible manual test procedure and an explicit non-adoption outcome if Sherpa does not pass it.

- [x] **Step 1: Write the failing documentation assertion**

~~~powershell
rg -n --fixed-strings 'speech-offline' README.md
rg -n --fixed-strings 'benchmark_stt.py' README.md
~~~

Expected: FAIL before editing because neither exact command is documented.

- [x] **Step 2: Run the assertion to verify it fails**

Run: rg -n --fixed-strings 'speech-offline' README.md; rg -n --fixed-strings 'benchmark_stt.py' README.md

Expected: no matching lines and non-zero status.

- [x] **Step 3: Write the minimal documentation**

Add a Voice input section documenting:

~~~powershell
& .\.venv\Scripts\python.exe -m pip install -e '.[speech-offline]'
& .\.venv\Scripts\python.exe scripts\benchmark_stt.py --engine sherpa-onnx --model-dir D:\LLM\models\sherpa --manifest D:\LLM\data\stt_samples.jsonl --output D:\LLM\data\stt_benchmark.jsonl
~~~

State the four required model filenames, that choosing Sherpa never sends audio
to Google, that no models are downloaded, and that it remains opt-in until a
20-sample Vietnamese/English/code-switch hardware gate passes. The gate records
success rate, final-transcript latency, and whether LLM/Gwen remain stable; it
must explicitly retain Google as default on failure.

- [x] **Step 4: Run verification**

Run: & .\.venv\Scripts\python.exe -m pytest -q

Expected: all retained tests pass.

Run: & .\.venv\Scripts\python.exe -m compileall -q src scripts

Expected: exit code 0.

Run: rg -n --fixed-strings 'speech-offline' README.md; rg -n --fixed-strings 'benchmark_stt.py' README.md

Expected: both commands locate the new documentation.

- [x] **Step 5: Commit**

~~~powershell
git add -- README.md
git commit -m "docs: document offline speech setup and benchmark"
~~~

## Plan self-review

- Spec coverage: Tasks 1-2 implement the selectable local-first boundary, privacy properties, typed failures, persistence, and unchanged Google default. Task 3 implements the caller-supplied WAV benchmark and optional dependency. Task 4 records installation, model-layout, manual gate, and regression checks. TTS, automatic download, GPU changes, diarization, and Tauri are explicitly excluded.
- Placeholder scan: no unfinished markers, vague error instructions, cross-task shorthand, or undefined interfaces remain. Deferred TTS is a scope boundary, not a planned implementation step.
- Type consistency: all tasks use CapturedAudio, SpeechEngineConfig, SpeechRecognitionError, and build_speech_recognizer from Task 1; speech_settings functions are produced in Task 2 and used only there.
