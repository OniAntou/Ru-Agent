from __future__ import annotations

import asyncio
import io
import wave
from hashlib import sha256

from harness.deterministic_worker import (
    _agent_step,
    _voice,
    collect_deterministic_stream,
    deterministic_worker_dependencies,
)
from harness.voice_fixtures import (
    build_synthetic_wav,
    inspect_wav,
    is_valid_transcription_fixture,
    is_valid_wav,
)

from ru_runtime.worker.v2.protocol import AGENT_STEP_DONE, validate_envelope


def _agent_prompt_context_payload() -> dict[str, object]:
    instruction = "root guidance"
    return {
        "schema_version": 1,
        "agent_profile": "ru-agent",
        "preset": "standard",
        "environment": {
            "model": "test-model",
            "platform": "windows",
            "locale": "vi-VN",
            "timezone": "Asia/Bangkok",
            "date": "2026-08-29",
            "workspace_label": "LLM",
        },
        "instruction_blocks": [
            {
                "source_id": "root:Ru.md",
                "display_name": "Ru.md",
                "content": instruction,
                "content_sha256": sha256(instruction.encode()).hexdigest(),
            }
        ],
        "capabilities": [
            {
                "id": "files.read",
                "version": "1.0.0",
                "summary": "Read a file",
                "input_schema_digest": "a" * 64,
                "policy_summary": "workspace.read",
                "availability": "enabled",
            }
        ],
        "runtime_context": {
            "memory": None,
            "mood": None,
            "voice": None,
            "vts": None,
            "hardware": None,
            "references": [],
        },
    }


def test_deterministic_agent_done_replies_include_prompt_receipts() -> None:
    prompt = f"receipt fixture {id(object())}"
    payload = {
        "prompt": prompt,
        "system_prompt_context": _agent_prompt_context_payload(),
        "settings": {
            "model_tools": [
                {
                    "tool_id": "files.read",
                    "tool_version": "1.0.0",
                    "description": "Read a file",
                    "input_schema": {"type": "object"},
                }
            ]
        },
    }

    async def collect() -> list[object]:
        first = [reply async for reply in _agent_step(payload, asyncio.Event())]
        second = [reply async for reply in _agent_step(payload, asyncio.Event())]
        return first + second

    replies = asyncio.run(collect())

    assert [reply.type for reply in replies] == [AGENT_STEP_DONE, AGENT_STEP_DONE]
    for sequence, reply in enumerate(replies, start=1):
        envelope = {
            "v": 2,
            "id": "step-1",
            "type": reply.type,
            "seq": sequence,
            "correlation_id": "run-1",
            "payload": dict(reply.payload),
        }
        assert validate_envelope(envelope)["type"] == AGENT_STEP_DONE
        assert "prompt_receipt" in reply.payload


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


def test_transcription_fixture_accepts_bounded_pcm_and_rejects_silence() -> None:
    assert is_valid_transcription_fixture(build_synthetic_wav()) is True

    silent = io.BytesIO()
    with wave.open(silent, "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(16_000)
        audio.writeframes(b"\x00\x00" * 3_200)
    assert is_valid_transcription_fixture(silent.getvalue()) is False


def test_synthetic_worker_never_uses_model_or_device_runtime() -> None:
    assert deterministic_worker_dependencies() == {
        "model": False,
        "microphone": False,
        "speaker": False,
        "vts": False,
    }


def test_deterministic_worker_voice_events_have_public_type_and_request_id() -> None:
    async def collect() -> list[object]:
        return [
            event
            async for event in _voice(
                {"operation": "stream", "request_id": "voice-1"},
                asyncio.Event(),
            )
        ]

    replies = asyncio.run(collect())

    assert replies[0].payload["type"] == "audio"
    assert replies[0].payload["request_id"] == "voice-1"
    assert replies[1].payload["type"] == "ended"
    assert replies[1].payload["request_id"] == "voice-1"
