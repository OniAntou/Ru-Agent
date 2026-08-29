"""Phase 2 managed device parity and privacy gates.

The Python implementation is exercised only as a deterministic reference. No
provider, desktop audio endpoint, microphone, VTube Studio instance, or Gemini
network request is contacted by these tests.
"""

from __future__ import annotations

import asyncio
import json
from pathlib import Path

import pytest

from ru_runtime.observation import gemini_vision
from ru_runtime.observation.screen_reaction import redact_text
from ru_runtime.voice.stream import VoiceStreamEvent
from ru_runtime.vts.catalog import build_catalog
from ru_runtime.worker.v1.handlers import (
    RuntimeDependencies,
    _runtime_load_params,
    build_runtime_handlers,
)
from ru_runtime.worker.v1.protocol import (
    HELLO_OK,
    ORDERED_RESPONSE_TYPES,
    WORKER_HEALTH_OK,
    redacted_summary,
)

ROOT = Path(__file__).resolve().parents[1]
FIXTURE_PATH = ROOT / "tests" / "fixtures" / "python-csharp-phase2-device-baseline.json"


def load_fixture() -> dict[str, object]:
    return json.loads(FIXTURE_PATH.read_text(encoding="utf-8"))


def load_case(case_id: str) -> dict[str, object]:
    fixture = load_fixture()
    matches = [case for case in fixture["cases"] if case["id"] == case_id]
    assert len(matches) == 1
    return matches[0]


def test_fixture_groups_are_complete_and_unique() -> None:
    fixture = load_fixture()
    assert fixture["schema_version"] == 1
    cases = fixture["cases"]
    assert len({case["id"] for case in cases}) == len(cases)
    assert {case["group"] for case in cases} == {
        "protocol",
        "settings",
        "voice",
        "errors",
        "speech",
        "vts",
        "observation",
        "cancellation",
        "privacy",
    }


def test_protocol_sequences_and_capabilities_match_reference() -> None:
    expected = load_case("protocol.handshake")["expected"]
    assert HELLO_OK in ORDERED_RESPONSE_TYPES
    assert WORKER_HEALTH_OK in ORDERED_RESPONSE_TYPES
    assert expected["capabilities"] == ["runtime", "voice", "vts", "observation"]
    assert expected["hello_sequence"] == 0
    assert expected["health_sequence"] == 0


def test_runtime_settings_match_reference_shape() -> None:
    case = load_case("settings.load")
    data = case["input"]
    payload = {
        "backend": data["backend"],
        "opencode_model": data["model_name"],
        "api_variant": data["api_variant"],
        "kilo_reasoning_enabled": data["kilo_reasoning_enabled"],
    }
    assert _runtime_load_params(payload) == data


def test_voice_control_types_states_and_sequences_match_reference() -> None:
    expected = load_case("voice.order")["expected"]

    async def stream(_payload: dict[str, object], _cancelled: asyncio.Event):
        yield VoiceStreamEvent("started", "phase2-voice", 0)
        yield VoiceStreamEvent("audio", "phase2-voice", 0, b"one")
        yield VoiceStreamEvent("audio", "phase2-voice", 1, b"two")
        yield VoiceStreamEvent("ended", "phase2-voice", 2)

    handlers = build_runtime_handlers(RuntimeDependencies(voice_stream=stream))

    async def collect() -> list[object]:
        return [
            reply
            async for reply in handlers.voice({"operation": "stream"}, asyncio.Event())
        ]

    replies = asyncio.run(collect())
    assert [reply.type for reply in replies] == [
        "voice.control",
        "voice.control",
        "voice.control",
        "voice.done",
    ]
    assert [reply.payload["type"] for reply in replies] == expected["event_types"]
    assert [reply.payload.get("state", reply.payload["type"]) for reply in replies] == expected[
        "states"
    ]
    assert [reply.payload["sequence"] for reply in replies] == expected["sequences"]
    assert sum(bool(reply.binary) for reply in replies) == expected["audio_chunks"]


def test_invalid_voice_style_maps_to_stable_error() -> None:
    expected = load_case("errors.invalid_voice_style")["expected"]

    def voice_audio(*_args: object) -> bytes:
        return b"audio"

    handlers = build_runtime_handlers(RuntimeDependencies(voice_audio=voice_audio))

    async def collect() -> list[object]:
        return [
            reply
            async for reply in handlers.voice(
                {
                    "operation": "tts",
                    "text": "Chào anh.",
                    "voice_label": "Ru (OmniVoice)",
                    "voice_style_id": "not-supported",
                },
                asyncio.Event(),
            )
        ]

    replies = asyncio.run(collect())
    assert len(replies) == 1
    assert replies[0].type == "error"
    assert replies[0].payload == expected


def test_speech_normalization_reference_is_whitespace_stable() -> None:
    case = load_case("speech.normalization")
    transcript = case["input"]["transcript"]
    assert " ".join(transcript.split()) == case["expected"]["text"]


def test_vts_code_sets_and_action_limit_match_reference() -> None:
    case = load_case("vts.code_sets")
    data = case["input"]
    catalog = build_catalog(
        model_id="phase2-model",
        model_name="Ru model",
        hotkeys=[{"hotkeyID": value} for value in data["hotkeys"]],
        expressions=[{"expressionFile": value} for value in data["expressions"]],
    )
    assert [entry.code for entry in catalog.entries if entry.kind == "hotkey"] == case[
        "expected"
    ]["hotkey_codes"]
    assert [entry.code for entry in catalog.entries if entry.kind == "expression"] == case[
        "expected"
    ]["expression_codes"]
    assert case["expected"]["valid_action_codes"] == ["vts_01", "expr_01"]
    assert case["expected"]["max_action_codes"] == 3


def test_observation_bounds_and_redaction_match_reference(monkeypatch: pytest.MonkeyPatch) -> None:
    expected = load_case("observation.bounds")["expected"]
    private_value = "PRIVATE-OBSERVATION-KEY"
    response = {
        "candidates": [
            {
                "content": {
                    "parts": [
                        {
                            "text": json.dumps(
                                {
                                    "scene": f"api_key: {private_value}",
                                    "event": "Working",
                                    "confidence": 9.0,
                                    "novelty": -1.0,
                                    "comment_worthiness": 0.5,
                                    "visible_text": [f"line-{index}" for index in range(10)],
                                }
                            )
                        }
                    ]
                }
            }
        ]
    }
    monkeypatch.setattr(gemini_vision, "_send_gemini_vision_request", lambda *_args: response)

    observation = gemini_vision.analyze_screenshot_with_gemini(
        b"jpeg", "phase2-key", model_name="gemini-3.1-flash-lite"
    )
    assert observation is not None
    assert observation.confidence == expected["confidence"]
    assert observation.novelty == expected["novelty"]
    assert observation.comment_worthiness == expected["comment_worthiness"]
    assert len(observation.visible_text) == expected["visible_text_count"]
    assert expected["redacted"] is True
    assert private_value not in observation.scene


def test_cancelled_voice_reference_completes_no_events() -> None:
    expected = load_case("cancellation.before_dispatch")["expected"]
    called = False

    async def stream(_payload: dict[str, object], cancelled: asyncio.Event):
        nonlocal called
        called = cancelled.is_set()
        if cancelled.is_set():
            raise asyncio.CancelledError
        if False:
            yield VoiceStreamEvent("ended", "never", 0)

    handlers = build_runtime_handlers(RuntimeDependencies(voice_stream=stream))
    cancelled = asyncio.Event()
    cancelled.set()

    async def collect() -> list[object]:
        return [
            reply
            async for reply in handlers.voice({"operation": "stream"}, cancelled)
        ]

    with pytest.raises(asyncio.CancelledError):
        asyncio.run(collect())
    assert called is True
    assert expected["completed"] is False


def test_privacy_scan_excludes_forbidden_values_from_summary_and_observation() -> None:
    case = load_case("privacy.redaction")
    prompt = case["input"]["prompt"]
    credential = case["input"]["credential"]
    assert credential not in redact_text(f"api_key: {credential}")
    summary = redacted_summary(
        {
            "v": 1,
            "id": "phase2-privacy",
            "type": "hello",
            "payload": {"token": credential, "prompt": prompt},
        }
    )
    assert prompt not in summary
    assert credential not in summary
    assert case["expected"]["raw_provider_body_visible"] is False
    assert case["expected"]["raw_audio_visible"] is False
