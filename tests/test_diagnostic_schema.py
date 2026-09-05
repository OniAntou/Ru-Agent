from __future__ import annotations

import json
from typing import Any

import pytest

from ru_runtime.persistence.diagnostic_schema import (
    ALLOWED_EVENT_CODES,
    DiagnosticEvent,
    deserialize_diagnostic_event,
    sanitize_diagnostic_event,
    serialize_diagnostic_event,
)


def test_serialize_diagnostic_event_accepts_allowlisted_fields_and_omits_nulls():
    event = DiagnosticEvent(
        event_id="evt_1234567890abcdef",
        ts=1720000000.123,
        iso="2026-08-20T00:00:00.123Z",
        level="warning",
        component="worker.voice",
        event_code="voice_unavailable",
        correlation_id="c_abcdef1234567890",
        duration_ms=42.5,
        status="failed",
        error_code="worker_unavailable",
        exception_kind="timeout",
    )

    serialized = serialize_diagnostic_event(event)
    data = json.loads(serialized)

    expected_keys = {
        "event_id",
        "ts",
        "iso",
        "level",
        "component",
        "event_code",
        "correlation_id",
        "duration_ms",
        "status",
        "error_code",
        "exception_kind",
    }
    assert set(data.keys()) == expected_keys
    assert data["event_id"] == "evt_1234567890abcdef"
    assert data["level"] == "warning"
    assert data["component"] == "worker.voice"
    assert data["event_code"] == "voice_unavailable"
    assert data["correlation_id"] == "c_abcdef1234567890"
    assert data["duration_ms"] == 42.5
    assert data["status"] == "failed"
    assert data["error_code"] == "worker_unavailable"
    assert data["exception_kind"] == "timeout"


def test_serialize_diagnostic_event_omits_none_fields():
    event = DiagnosticEvent(
        event_id="evt_min",
        ts=1720000000.0,
        iso="2026-08-20T00:00:00.000Z",
        level="info",
        component="worker",
        event_code="worker_startup",
    )
    serialized = serialize_diagnostic_event(event)
    data = json.loads(serialized)

    assert "correlation_id" not in data
    assert "duration_ms" not in data
    assert "status" not in data
    assert "error_code" not in data
    assert "exception_kind" not in data
    assert "tool_id" not in data
    assert "tool_version" not in data
    assert "verification_state" not in data
    assert "endpoint_role" not in data
    assert "endpoint_hash" not in data


def test_serialize_diagnostic_event_emits_exact_bounded_action_proof_shape():
    event = DiagnosticEvent(
        event_id="evt_action_1",
        ts=1720000000.0,
        iso="2026-08-22T00:00:00.000Z",
        level="info",
        component="host.agent",
        event_code="tool.verification.succeeded",
        correlation_id="c_abcdef1234567890",
        duration_ms=12.0,
        status="succeeded",
        tool_id="files.write",
        tool_version="1.0.0",
        verification_state="verified",
        endpoint_role="default_render_multimedia",
        endpoint_hash="0123456789abcdef",
    )

    data = json.loads(serialize_diagnostic_event(event))

    assert set(data) == {
        "event_id", "ts", "iso", "level", "component", "event_code",
        "correlation_id", "duration_ms", "status", "tool_id", "tool_version",
        "verification_state", "endpoint_role", "endpoint_hash",
    }
    assert data["tool_id"] == "files.write"
    assert data["tool_version"] == "1.0.0"
    assert data["verification_state"] == "verified"
    assert data["endpoint_role"] == "default_render_multimedia"
    assert data["endpoint_hash"] == "0123456789abcdef"


@pytest.mark.parametrize(
    ("field", "value"),
    (
        ("tool_id", "x" * 129),
        ("tool_id", "system volume"),
        ("tool_id", "system/volume"),
        ("tool_id", "Bearer_secret"),
        ("tool_id", "https://example.com"),
        ("tool_id", r"C:\Windows\audio"),
        ("tool_id", "system@volume"),
        ("tool_version", "1.0"),
        ("tool_version", "01.0.0"),
        ("tool_version", "1.0.0-beta"),
        ("verification_state", "unknown"),
        ("verification_state", "verified now"),
        ("endpoint_role", "communications"),
        ("endpoint_role", "default_render_console"),
        ("endpoint_hash", "0123456789ABCDEf"),
        ("endpoint_hash", "0123456789abcdeg"),
        ("endpoint_hash", "0123456789abcde"),
        ("endpoint_hash", "0123456789abcdef0"),
    ),
)
def test_sanitize_diagnostic_event_drops_invalid_action_proof(field: str, value: str):
    values: dict[str, Any] = {
        "tool_id": "files.write",
        "tool_version": "1.0.0",
        "verification_state": "verified",
        "endpoint_role": "default_render_multimedia",
        "endpoint_hash": "0123456789abcdef",
    }
    values[field] = value
    event = DiagnosticEvent(
        event_id="evt_action_invalid",
        ts=1720000000.0,
        iso="2026-08-22T00:00:00.000Z",
        level="info",
        component="worker.runtime",
        event_code="tool.receipt.rejected",
        **values,
    )

    sanitized = sanitize_diagnostic_event(event)

    assert getattr(sanitized, field) is None


@pytest.mark.parametrize("status", ("denied", "approval_required", "timed_out", "timeout"))
def test_action_and_legacy_terminal_statuses_are_preserved(status: str):
    event = DiagnosticEvent(
        event_id="evt_status",
        ts=1720000000.0,
        iso="2026-08-22T00:00:00.000Z",
        level="info",
        component="worker.runtime",
        event_code="tool.receipt.rejected",
        status=status,
    )

    assert sanitize_diagnostic_event(event).status == status


@pytest.mark.parametrize(
    "event_code",
    (
        "tool.intent.resolved",
        "tool.intent.clarification",
        "tool.intent.rejected",
    ),
)
def test_deterministic_intent_event_codes_are_allowlisted(event_code: str) -> None:
    assert event_code in ALLOWED_EVENT_CODES


def test_sanitize_diagnostic_event_redacts_dangerous_or_unapproved_values():
    event = DiagnosticEvent(
        event_id="",
        ts=0,
        iso="",
        level="DANGEROUS_LEVEL",
        component="unapproved_comp_with_url_http://secret.com",
        event_code="UNAPPROVED!CODE@#$",
        correlation_id="invalid id with Bearer 12345 secret and spaces",
        duration_ms=-10.0,
        status="INVALID_STATUS",
        error_code="secret api key sk-1234567890abcdef1234567890",
        exception_kind="custom_exception_with_stacktrace_at_line_42",
    )
    sanitized = sanitize_diagnostic_event(event)

    assert sanitized.event_id.startswith("evt_")
    assert sanitized.ts > 0
    assert sanitized.iso.endswith("Z")
    assert sanitized.level == "info"
    assert sanitized.component == "worker"
    assert sanitized.event_code == "diagnostic_unclassified"
    assert sanitized.correlation_id is not None and sanitized.correlation_id.startswith("c_")
    assert sanitized.duration_ms is None
    assert sanitized.status is None
    assert sanitized.error_code is None
    assert sanitized.exception_kind == "unknown"

    serialized = serialize_diagnostic_event(sanitized)
    assert "secret" not in serialized.lower()
    assert "bearer" not in serialized.lower()
    assert "http" not in serialized.lower()
    assert "stacktrace" not in serialized.lower()


def test_deserialize_filters_unknown_fields_and_messages():
    raw_json = json.dumps({
        "event_id": "evt_worker_1",
        "ts": 1720000000.5,
        "iso": "2026-08-20T00:00:00.500Z",
        "level": "error",
        "component": "worker.runtime",
        "event_code": "worker_unavailable",
        "message": "Dangerous prompt content with session_id s_12345 and secret",
        "traceback": "Traceback (most recent call last):\n  File 'foo.py', line 10",
        "secret_token": "sk-12345",
    })

    event = deserialize_diagnostic_event(raw_json)
    assert event is not None
    assert event.event_id == "evt_worker_1"
    assert event.level == "error"
    assert event.component == "worker.runtime"
    assert event.event_code == "worker_unavailable"

    re_serialized = serialize_diagnostic_event(event)
    assert "message" not in re_serialized
    assert "traceback" not in re_serialized
    assert "secret_token" not in re_serialized
    assert "session_id" not in re_serialized
    assert "Dangerous" not in re_serialized


def test_deserialize_drops_every_private_action_field():
    forbidden = {
        "arguments", "output", "muted", "prompt", "reply", "path", "script",
        "stdout", "stderr", "device_name", "exception_message",
    }
    raw = {
        "event_id": "evt_private",
        "ts": 1720000000.0,
        "iso": "2026-08-22T00:00:00.000Z",
        "level": "info",
        "component": "worker.runtime",
        "event_code": "tool.receipt.rejected",
        **dict.fromkeys(forbidden, r"Bearer C:\secret https://example.com"),
    }

    event = deserialize_diagnostic_event(json.dumps(raw))
    assert event is not None
    serialized = serialize_diagnostic_event(event)
    data = json.loads(serialized)

    assert forbidden.isdisjoint(data)
    assert "bearer" not in serialized.casefold()
    assert "secret" not in serialized.casefold()


def test_dotted_event_codes_accepted():
    event = DiagnosticEvent(
        event_id="evt_test_1",
        ts=1000.0,
        iso="2026-08-20T00:00:00Z",
        level="info",
        component="worker.voice",
        event_code="speech.synthesize.start",
    )
    sanitized = sanitize_diagnostic_event(event)
    assert sanitized.event_code == "speech.synthesize.start"
