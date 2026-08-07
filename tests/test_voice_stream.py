from __future__ import annotations

import asyncio
import threading

import pytest

from mini_gpt.voice_observability import VoiceTiming
from mini_gpt.voice_stream import VoiceStreamCoordinator, VoiceStreamEvent


def collect_events(coordinator: VoiceStreamCoordinator, **kwargs) -> list[VoiceStreamEvent]:
    async def collect() -> list[VoiceStreamEvent]:
        return [event async for event in coordinator.start(**kwargs)]

    return asyncio.run(collect())


def test_voice_stream_coordinator_emits_ordered_audio_without_private_markers() -> None:
    prepared: list[str] = []
    requested: list[str] = []

    coordinator = VoiceStreamCoordinator(
        prepare_speech=lambda selection: prepared.append(selection.profile_id),
        request_audio=lambda text, voice_label, speed, style_id: (
            requested.append(text) or text.encode()
        ),
        memory_lock=threading.Lock(),
    )

    events = collect_events(
        coordinator,
        request_id="r1",
        text="Một câu. [[VOICE_PACE:slow]] Câu thứ hai.",
        voice_label="Ru (OmniVoice)",
        speed=1.0,
    )

    assert [event.event_type for event in events] == ["started", "audio", "audio", "ended"]
    assert [event.sequence for event in events] == [0, 0, 1, 2]
    assert [event.audio for event in events if event.audio is not None] == [
        "Một câu.".encode(),
        "Câu thứ hai.".encode(),
    ]
    assert prepared == ["ru"]
    assert requested == ["Một câu.", "Câu thứ hai."]


def test_voice_stream_coordinator_discards_future_chunks_after_cancel() -> None:
    requested: list[str] = []
    coordinator: VoiceStreamCoordinator

    def request_audio(
        text: str,
        voice_label: str,
        speed: float,
        style_id: str,
    ) -> bytes:
        requested.append(text)
        if len(requested) == 1:
            coordinator.cancel("r2")
        return text.encode()

    coordinator = VoiceStreamCoordinator(
        prepare_speech=lambda selection: None,
        request_audio=request_audio,
        memory_lock=threading.Lock(),
    )

    events = collect_events(
        coordinator,
        request_id="r2",
        text="Một câu. Câu thứ hai.",
        voice_label="Ru (OmniVoice)",
        speed=1.0,
    )

    assert [event.event_type for event in events] == ["started", "cancelled"]
    assert requested == ["Một câu."]


def test_voice_stream_coordinator_forwards_one_style_to_every_chunk() -> None:
    received: list[tuple[str, str]] = []
    coordinator = VoiceStreamCoordinator(
        prepare_speech=lambda selection: None,
        request_audio=lambda text, voice_label, speed, style_id: (
            received.append((text, style_id)) or text.encode()
        ),
        memory_lock=threading.Lock(),
    )

    events = collect_events(
        coordinator,
        request_id="style-1",
        text="Một câu. Câu thứ hai.",
        voice_label="Ru (OmniVoice)",
        speed=1.0,
        voice_style_id="whisper",
    )

    assert [event.event_type for event in events] == [
        "started",
        "audio",
        "audio",
        "ended",
    ]
    assert received == [
        ("Một câu.", "whisper"),
        ("Câu thứ hai.", "whisper"),
    ]


def test_voice_timing_serialization_has_no_text_or_negative_values() -> None:
    timing = VoiceTiming("r3", "ru", "vi-normalized-16")
    timing.record("first_audio_ms", 12.5)

    assert timing.to_dict() == {
        "request_id": "r3",
        "voice_profile": "ru",
        "config_id": "vi-normalized-16",
        "timings_ms": {"first_audio_ms": 12.5},
    }
    with pytest.raises(ValueError):
        timing.record("bad", -1)


def test_voice_playback_acknowledgements_are_current_and_terminal_once() -> None:
    coordinator = VoiceStreamCoordinator(
        prepare_speech=lambda selection: None,
        request_audio=lambda text, voice_label, speed, style_id: text.encode(),
        memory_lock=threading.Lock(),
    )
    collect_events(
        coordinator,
        request_id="r4",
        text="Một câu.",
        voice_label="Ru (OmniVoice)",
        speed=1.0,
    )

    assert coordinator.acknowledge_playback("r4", "started") is True
    assert coordinator.acknowledge_playback("r4", "started") is False
    assert coordinator.acknowledge_playback("old", "ended") is False
    assert coordinator.acknowledge_playback("r4", "ended") is True
    assert coordinator.acknowledge_playback("r4", "failed") is False
    assert coordinator.timing_snapshot("r4")["timings_ms"]

    collect_events(
        coordinator,
        request_id="r5",
        text="Câu mới.",
        voice_label="Ru (OmniVoice)",
        speed=1.0,
    )
    assert coordinator.acknowledge_playback("r4", "ended") is False
