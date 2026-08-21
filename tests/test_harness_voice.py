from __future__ import annotations

from harness.deterministic_worker import (
    collect_deterministic_stream,
    deterministic_worker_dependencies,
)
from harness.voice_fixtures import build_synthetic_wav, inspect_wav, is_valid_wav


def test_synthetic_wav_is_mono_pcm_with_bounded_nonzero_duration() -> None:
    payload = build_synthetic_wav()

    assert inspect_wav(payload) == {
        "channels": 1,
        "sample_width": 2,
        "sample_rate": 16_000,
        "frames": 3_200,
        "valid": True,
    }


def test_synthetic_tts_stream_emits_audio_then_one_ended_event() -> None:
    events = list(collect_deterministic_stream(request_id="voice-1"))

    assert [event.type for event in events] == ["audio", "ended"]
    assert events[0].binary is not None
    assert is_valid_wav(events[0].binary) is True
    assert events[-1].request_id == "voice-1"


def test_synthetic_worker_never_uses_model_or_device_runtime() -> None:
    assert deterministic_worker_dependencies() == {
        "model": False,
        "microphone": False,
        "speaker": False,
        "vts": False,
    }
