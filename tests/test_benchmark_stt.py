from __future__ import annotations

from pathlib import Path
import wave

import pytest

from ru_runtime.speech_runtime import CapturedAudio, SpeechConfigurationError
from scripts.benchmark_stt import BenchmarkItem, load_wav, run_benchmark


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
        [item],
        FakeRecognizer(),
        load_audio=lambda _path: CapturedAudio(b"pcm"),
        clock=iter((1.0, 1.125)).__next__,
    )

    assert records == [
        {
            "engine_id": "fake",
            "audio_path": str(wav_path),
            "expected_text": "xin chao",
            "transcript": "xin chao",
            "latency_ms": 125.0,
            "status": "ok",
        }
    ]


def test_benchmark_wav_loader_accepts_only_16khz_mono_pcm(tmp_path: Path) -> None:
    wav_path = tmp_path / "sample.wav"
    with wave.open(str(wav_path), "wb") as output_wav:
        output_wav.setnchannels(1)
        output_wav.setsampwidth(2)
        output_wav.setframerate(16_000)
        output_wav.writeframes(b"\x00\x00")

    assert load_wav(wav_path) == CapturedAudio(b"\x00\x00")

    stereo_path = tmp_path / "stereo.wav"
    with wave.open(str(stereo_path), "wb") as output_wav:
        output_wav.setnchannels(2)
        output_wav.setsampwidth(2)
        output_wav.setframerate(16_000)
        output_wav.writeframes(b"\x00\x00\x00\x00")

    with pytest.raises(SpeechConfigurationError, match="mono"):
        load_wav(stereo_path)
