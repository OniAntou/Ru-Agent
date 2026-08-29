"""Phase 1 golden parity gates between the Python reference and the fixture.

Every assertion re-derives approved fixture fields with deterministic
reference functions only.  No models, network, audio devices, or VTube Studio
state are contacted.
"""

from __future__ import annotations

import asyncio
import json
from pathlib import Path

import pytest

from ru_runtime.generation.prompt_context import parse_system_prompt_context, sha256_utf8
from ru_runtime.generation.system_prompt_composer import compose_system_prompt
from ru_runtime.generation.tool_calls import parse_tool_call
from ru_runtime.worker.v1.protocol import ProtocolError, redacted_summary, validate_envelope
from ru_runtime.worker.v2.model_step import StepGenerationError, run_model_step

ROOT = Path(__file__).resolve().parents[1]
FIXTURE_PATH = ROOT / "tests/fixtures/python-csharp-phase1-baseline.json"


def load_fixture() -> dict:
    return json.loads(FIXTURE_PATH.read_text(encoding="utf-8"))


def load_case(fixture: dict, case_id: str) -> dict:
    matches = [case for case in fixture["cases"] if case["id"] == case_id]
    assert len(matches) == 1, f"fixture must contain exactly one {case_id} case"
    return matches[0]


def test_prompt_case_matches_the_python_reference() -> None:
    fixture = load_fixture()
    case = load_case(fixture, "prompt.basic")
    expected = case["expected"]

    context = parse_system_prompt_context(case["input"])
    snapshot = compose_system_prompt(context)

    assert snapshot.receipt.composer_version == expected["composer_version"]
    assert sha256_utf8(snapshot.text) == expected["prompt_sha256"]
    assert snapshot.receipt.prompt_sha256 == expected["prompt_sha256"]
    digests = [
        {"block_id": block.block_id, "sha256": block.sha256}
        for block in snapshot.receipt.block_digests
    ]
    assert digests == expected["block_digests"]
    # Instruction digests stay bound to the context that declared them.
    for instruction in context.instruction_blocks:
        bound = next(
            item
            for item in digests
            if item["block_id"] == f"instruction.{instruction.source_id}"
        )
        assert bound["sha256"] == instruction.content_sha256

    marker = expected["private_marker"]
    assert marker not in snapshot.text
    assert marker not in json.dumps(snapshot.receipt.to_payload(), ensure_ascii=False)


def test_tool_selection_case_matches_the_python_reference() -> None:
    fixture = load_fixture()
    case = load_case(fixture, "tool_selection.valid")
    case_input = case["input"]

    selection = parse_tool_call(case_input["text"])

    assert selection is not None
    assert case["expected"]["accepted"] is True
    assert selection.tool_id == case_input["tool_id"]
    assert selection.tool_version == case_input["tool_version"]
    assert selection.arguments == case_input["arguments"]
    assert selection.tool_id in case_input["advertised"]


def test_protocol_case_maps_to_a_bounded_non_retryable_code() -> None:
    fixture = load_fixture()
    case = load_case(fixture, "protocol.unknown_type")
    expected = case["expected"]

    with pytest.raises(ProtocolError) as excinfo:
        validate_envelope(case["input"]["envelope"])

    assert excinfo.value.code == expected["code"]
    # The v1 error payload shape is exactly {code, retryable} and unknown
    # types are never retryable.
    assert expected == {"code": "unknown_type", "retryable": False}


def test_cancellation_case_completes_nothing_once_cancelled() -> None:
    fixture = load_fixture()
    case = load_case(fixture, "cancellation.before_dispatch")
    assert case["expected"]["completed"] is False

    prompt_case = load_case(fixture, "prompt.basic")
    payload = {
        "prompt": case["input"]["prompt"],
        "turns": [],
        "system_prompt_context": prompt_case["input"],
        "settings": {},
    }

    deltas: list[str] = []

    def runner(**kwargs: object) -> str:
        if kwargs["is_cancelled"]():
            raise asyncio.CancelledError
        return "must never be used"

    async def scenario() -> None:
        event = asyncio.Event()
        event.set()
        await run_model_step(runner, payload, event, on_delta=deltas.append)

    with pytest.raises(asyncio.CancelledError):
        asyncio.run(scenario())

    assert deltas == []


def test_privacy_case_forbids_prompt_and_credential_leakage() -> None:
    fixture = load_fixture()
    case = load_case(fixture, "privacy.redaction")
    probes = (case["input"]["prompt_text"], case["input"]["credential"])

    # Prompt receipts are digest-only: neither the composed prompt text nor
    # the privacy probes can appear in the receipt payload.
    prompt_case = load_case(fixture, "prompt.basic")
    snapshot = compose_system_prompt(parse_system_prompt_context(prompt_case["input"]))
    receipt_json = json.dumps(snapshot.receipt.to_payload(), ensure_ascii=False)
    for probe in probes:
        assert probe not in receipt_json

    # Step failures expose bounded stable codes only.
    error = StepGenerationError("invalid_request")
    assert str(error) == "invalid_request"
    assert error.code == "invalid_request"
    assert error.retryable is False
    for probe in probes:
        assert probe not in str(error)

    # v1 worker summaries redact envelope content before reaching logs.
    hello_with_private_text = {
        "v": 1,
        "id": "privacy-probe-1",
        "type": "hello",
        "payload": {
            "token": case["input"]["credential"],
            "text": case["input"]["prompt_text"],
            "capabilities": ["runtime"],
        },
    }
    summary = redacted_summary(hello_with_private_text)
    for probe in probes:
        assert probe not in summary
