from __future__ import annotations

import asyncio
import threading
import time

import pytest

import ru_runtime.voice.stream as voice_stream
from ru_runtime.voice.observability import VoiceTiming
from ru_runtime.voice.stream import VoiceStreamCoordinator, VoiceStreamEvent


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


def test_voice_stream_coordinator_forwards_original_style_to_every_chunk() -> None:
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
        voice_style_id="original",
    )

    assert [event.event_type for event in events] == [
        "started",
        "audio",
        "audio",
        "ended",
    ]
    assert received == [
        ("Một câu.", "original"),
        ("Câu thứ hai.", "original"),
    ]


def test_voice_timeout_keeps_model_lock_until_background_worker_exits() -> None:
    worker_started = threading.Event()
    allow_worker_exit = threading.Event()
    memory_lock = threading.Lock()

    def slow_request_audio(text, voice_label, speed, style_id) -> bytes:
        worker_started.set()
        assert allow_worker_exit.wait(timeout=2)
        return b"audio"

    coordinator = VoiceStreamCoordinator(
        prepare_speech=lambda selection: None,
        request_audio=slow_request_audio,
        memory_lock=memory_lock,
        # Leave enough room for a cold Python 3.11 thread-pool startup; the
        # audio worker still blocks for two seconds, so this deterministically
        # exercises the synthesis-timeout cleanup path.
        request_timeout=0.25,
    )

    async def collect() -> list[VoiceStreamEvent]:
        return [
            event
            async for event in coordinator.start(
                request_id="timeout-lock",
                text="Mot cau.",
                voice_label="Ru (OmniVoice)",
                speed=1.0,
            )
        ]

    loop = asyncio.new_event_loop()
    try:
        events = loop.run_until_complete(collect())
        assert worker_started.is_set()
        assert [event.event_type for event in events] == ["started", "failed"]
        assert events[-1].error_code == "synthesis_timeout"
        assert memory_lock.locked()

        allow_worker_exit.set()
        deadline = time.monotonic() + 1
        while memory_lock.locked() and time.monotonic() < deadline:
            loop.run_until_complete(asyncio.sleep(0.01))
        assert not memory_lock.locked()
    finally:
        allow_worker_exit.set()
        loop.run_until_complete(loop.shutdown_default_executor())
        loop.close()


def test_cancelled_worker_wrapper_keeps_model_lock_until_thread_exits(monkeypatch) -> None:
    worker_started = threading.Event()
    allow_worker_exit = threading.Event()
    memory_lock = threading.Lock()
    real_create_task = asyncio.create_task
    worker_tasks: list[asyncio.Task[object]] = []

    def capture_worker_task(coroutine) -> asyncio.Task[object]:
        task = real_create_task(coroutine)
        worker_tasks.append(task)
        return task

    def slow_request_audio(text, voice_label, speed, style_id) -> bytes:
        worker_started.set()
        assert allow_worker_exit.wait(timeout=2)
        return b"audio"

    monkeypatch.setattr(voice_stream.asyncio, "create_task", capture_worker_task)
    coordinator = VoiceStreamCoordinator(
        prepare_speech=lambda selection: None,
        request_audio=slow_request_audio,
        memory_lock=memory_lock,
        request_timeout=1,
    )

    async def collect() -> list[VoiceStreamEvent]:
        return [
            event
            async for event in coordinator.start(
                request_id="cancelled-worker",
                text="Mot cau.",
                voice_label="Ru (OmniVoice)",
                speed=1.0,
            )
        ]

    async def cancel_worker_after_start() -> None:
        collector = real_create_task(collect())
        deadline = time.monotonic() + 1
        while not worker_started.is_set() and time.monotonic() < deadline:
            await asyncio.sleep(0.01)
        assert worker_started.is_set()
        worker_tasks[-1].cancel()
        with pytest.raises(asyncio.CancelledError):
            await collector

    loop = asyncio.new_event_loop()
    try:
        loop.run_until_complete(cancel_worker_after_start())
        assert memory_lock.locked()

        allow_worker_exit.set()
        deadline = time.monotonic() + 1
        while memory_lock.locked() and time.monotonic() < deadline:
            loop.run_until_complete(asyncio.sleep(0.01))
        assert not memory_lock.locked()
    finally:
        allow_worker_exit.set()
        loop.run_until_complete(loop.shutdown_default_executor())
        loop.close()


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
