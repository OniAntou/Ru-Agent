"""Small, deterministic PCM fixtures for protocol-only voice checks."""

from __future__ import annotations

import io
import math
import struct
import wave
from typing import TypedDict

MAX_WAV_BYTES = 256 * 1024
SYNTHETIC_SAMPLE_RATE = 16_000
SYNTHETIC_FRAMES = 3_200


class WavInfo(TypedDict):
    channels: int
    sample_width: int
    sample_rate: int
    frames: int
    valid: bool


def build_synthetic_wav() -> bytes:
    """Return a stable 200 ms mono PCM fixture with a silent tail."""

    samples: list[int] = []
    for index in range(SYNTHETIC_FRAMES):
        if index >= 2_400:
            samples.append(0)
            continue
        carrier = math.sin(2.0 * math.pi * 220.0 * index / SYNTHETIC_SAMPLE_RATE)
        overtone = 0.35 * math.sin(2.0 * math.pi * 440.0 * index / SYNTHETIC_SAMPLE_RATE)
        samples.append(int(8_000 * (carrier + overtone)))
    output = io.BytesIO()
    with wave.open(output, "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(SYNTHETIC_SAMPLE_RATE)
        audio.writeframes(b"".join(struct.pack("<h", sample) for sample in samples))
    return output.getvalue()


def inspect_wav(payload: bytes) -> WavInfo:
    invalid: WavInfo = {
        "channels": 0,
        "sample_width": 0,
        "sample_rate": 0,
        "frames": 0,
        "valid": False,
    }
    if not isinstance(payload, bytes) or len(payload) > MAX_WAV_BYTES:
        return invalid
    try:
        with wave.open(io.BytesIO(payload), "rb") as audio:
            info: WavInfo = {
                "channels": audio.getnchannels(),
                "sample_width": audio.getsampwidth(),
                "sample_rate": audio.getframerate(),
                "frames": audio.getnframes(),
                "valid": False,
            }
            info["valid"] = (
                info["channels"] == 1
                and info["sample_width"] == 2
                and info["sample_rate"] == SYNTHETIC_SAMPLE_RATE
                and info["frames"] > 0
            )
            return info
    except (OSError, EOFError, wave.Error):
        return invalid


def is_valid_wav(payload: bytes) -> bool:
    return inspect_wav(payload)["valid"]


def is_valid_transcription_fixture(payload: bytes) -> bool:
    info = inspect_wav(payload)
    if not info["valid"] or info["frames"] > 20_000:
        return False
    try:
        with wave.open(io.BytesIO(payload), "rb") as audio:
            return any(audio.readframes(info["frames"]))
    except (OSError, EOFError, wave.Error):
        return False
