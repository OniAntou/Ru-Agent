from __future__ import annotations

import json
import pytest

from ru_runtime.persistence.diagnostic_schema import (
    DiagnosticEvent,
    serialize_diagnostic_event,
    sanitize_diagnostic_event,
    deserialize_diagnostic_event,
    ALLOWED_LEVELS,
    ALLOWED_COMPONENTS,
    ALLOWED_EVENT_CODES,
    ALLOWED_STATUSES,
    ALLOWED_EXCEPTION_KINDS,
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
    assert sanitized.correlation_id.startswith("c_")
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
