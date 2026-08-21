from __future__ import annotations

import json
import os
import shutil
import tempfile
import time
from pathlib import Path

from ru_runtime.persistence.activity_outbox import (
    ActivityOutboxWriter,
    format_activity_preview,
    sanitize_activity_event,
)


def test_format_activity_preview_truncates_and_normalizes():
    text = "  Hello   world!  \nThis is a long sentence with secrets like https://api.openai.com/v1/key=sk-1234567890abcdef1234567890 and Bearer token123  "
    preview, orig_len, redacted = format_activity_preview(text, max_length=50)

    assert len(preview) <= 50
    assert orig_len == len(text)
    assert redacted is True
    assert "https://" not in preview
    assert "Bearer" not in preview
    assert "\n" not in preview


def test_sanitize_activity_event_chat_replaces_content_with_preview():
    raw_event = {
        "type": "chat",
        "session_id": "sess_123",
        "data": {
            "role": "user",
            "content": "A secret message sk-12345",
        },
    }
    sanitized = sanitize_activity_event(raw_event)

    assert sanitized["event_id"].startswith("evt_")
    assert "ts" in sanitized
    assert "iso" in sanitized
    assert sanitized["type"] == "chat"
    assert sanitized["session_id"] == "sess_123"
    assert "content" not in sanitized["data"]
    assert "preview" in sanitized["data"]
    assert sanitized["data"]["role"] == "user"
    assert sanitized["data"]["original_length"] == len("A secret message sk-12345")


def test_activity_outbox_writer_writes_jsonl():
    temp_dir = tempfile.mkdtemp(prefix="ru_outbox_test_")
    try:
        writer = ActivityOutboxWriter(log_dir=temp_dir, flush_interval_seconds=0.05)
        raw_event = {
            "type": "chat",
            "session_id": "sess_1",
            "data": {"role": "assistant", "content": "hello there!"},
        }

        assert writer.try_enqueue(raw_event) is True
        writer.flush(timeout_seconds=2.0)
        writer.close()

        outbox_file = Path(temp_dir) / "activity_outbox.jsonl"
        assert outbox_file.is_file()

        lines = outbox_file.read_text(encoding="utf-8").strip().splitlines()
        assert len(lines) == 1

        record = json.loads(lines[0])
        assert record["type"] == "chat"
        assert "content" not in record["data"]
        assert record["data"]["preview"] == "hello there!"
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_activity_outbox_reports_overflow_without_blocking_enqueue():
    temp_dir = tempfile.mkdtemp(prefix="ru_outbox_overflow_")
    try:
        writer = ActivityOutboxWriter(
            log_dir=temp_dir,
            capacity=10,
            flush_interval_seconds=10.0,
        )
        event = {
            "type": "chat",
            "session_id": "sess_1",
            "data": {"role": "user", "content": "hello"},
        }

        for index in range(10):
            assert writer.try_enqueue({**event, "event_id": f"evt_{index}"}) is True
        assert writer.try_enqueue({**event, "event_id": "evt_second"}) is True
        assert writer.dropped_count == 1
        writer.close()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)
