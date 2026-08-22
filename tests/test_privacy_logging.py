from __future__ import annotations

import json
from pathlib import Path
import pytest

from ru_runtime.persistence.diagnostic_schema import (
    DiagnosticEvent,
    serialize_diagnostic_event,
    sanitize_diagnostic_event,
)
from ru_runtime.persistence.diagnostic_log import (
    DiagnosticLogWriter,
    reset_global_diagnostic_writer,
)
from ru_runtime.persistence.activity_outbox import (
    ActivityOutboxWriter,
    format_activity_preview,
    sanitize_activity_event,
)


def test_privacy_scan_diagnostic_events_never_contain_freeform_message_or_trace():
    evt = DiagnosticEvent(
        event_id="evt_test",
        ts=1000.0,
        iso="2026-08-20T00:00:00Z",
        level="error",
        component="worker.core",
        event_code="chat_failed",
        correlation_id="c_1234567890abcdef",
        duration_ms=100.0,
        status="failed",
        error_code="rate_limited",
        exception_kind="network",
    )
    serialized = serialize_diagnostic_event(evt)
    parsed = json.loads(serialized)

    forbidden_keys = {
        "message", "msg", "arguments", "output", "level_value", "muted", "prompt",
        "reply", "response", "content", "path", "script", "stdout", "stderr",
        "device_name", "exception_message", "stack_trace", "traceback", "args",
    }
    for key in forbidden_keys:
        assert key not in parsed


def test_privacy_scan_injected_action_values_never_survive_deserialization():
    forbidden_keys = {
        "arguments", "output", "muted", "prompt", "reply", "path", "script",
        "stdout", "stderr", "device_name", "exception_message",
    }
    raw = {
        "event_id": "evt_injected",
        "ts": 1000.0,
        "iso": "2026-08-20T00:00:00Z",
        "level": "error",
        "component": "worker.runtime",
        "event_code": "tool.receipt.rejected",
        **{key: r"Bearer C:\secret https://example.com" for key in forbidden_keys},
    }
    from ru_runtime.persistence.diagnostic_schema import deserialize_diagnostic_event

    event = deserialize_diagnostic_event(json.dumps(raw))
    assert event is not None
    serialized = serialize_diagnostic_event(event)
    parsed = json.loads(serialized)

    assert forbidden_keys.isdisjoint(parsed)
    assert "bearer" not in serialized.casefold()
    assert "secret" not in serialized.casefold()


def test_privacy_scan_activity_preview_masks_secrets_urls_paths():
    secret_text = "API Key: sk-abcdef1234567890 and Bearer eyJhbGciOi at https://api.openai.com/v1 with C:\\Windows\\System32"
    preview, orig_len, redacted = format_activity_preview(secret_text)

    assert redacted is True
    assert "sk-abcdef1234567890" not in preview
    assert "eyJhbGciOi" not in preview
    assert "https://api.openai.com" not in preview
    assert "C:\\Windows" not in preview
    assert "[REDACTED_KEY]" in preview
    assert "[REDACTED_TOKEN]" in preview
    assert "[URL]" in preview
    assert "[PATH]" in preview


def test_privacy_scan_written_diagnostic_file(tmp_path: Path):
    writer = DiagnosticLogWriter(log_dir=str(tmp_path))
    try:
        writer.try_enqueue(
            DiagnosticEvent(
                event_id="evt_p1",
                ts=1000.0,
                iso="2026-08-20T00:00:00Z",
                level="info",
                component="worker.voice",
                event_code="voice_started",
                correlation_id="c_1234567890abcdef",
            )
        )
        writer.flush(timeout_seconds=1.0)

        log_files = list(tmp_path.glob("*.jsonl"))
        assert len(log_files) == 1
        content = log_files[0].read_text(encoding="utf-8")

        assert "evt_p1" in content
        assert "voice_started" in content
        assert "message" not in content
        assert "prompt" not in content
    finally:
        writer.close()
