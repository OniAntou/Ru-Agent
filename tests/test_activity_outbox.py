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


def test_sanitize_activity_event_observation_reaction_bounds_and_redacts_preview():
    text = "React [[SCREENSHOT:private]] Bearer token123 and sk-1234567890abcdef"
    sanitized = sanitize_activity_event(
        {
            "type": "observation_reaction",
            "session_id": "sess_123",
            "data": {
                "text": text,
                "scene": "s" * 150,
                "event": "e" * 150,
                "observation_id": "o" * 150,
                "private_payload": "must not persist",
            },
        }
    )

    data = sanitized["data"]
    assert sanitized["type"] == "observation_reaction"
    assert data["original_length"] == len(text)
    assert data["redacted"] is True
    assert "[[SCREENSHOT" not in data["preview"]
    assert "Bearer token123" not in data["preview"]
    assert "sk-1234567890abcdef" not in data["preview"]
    assert len(data["preview"]) <= 120
    assert len(data["scene"]) <= 100
    assert len(data["event"]) <= 100
    assert len(data["observation_id"]) <= 64
    assert "private_payload" not in data


def test_sanitize_activity_event_original_length_uses_bounded_fallback_for_invalid_metadata():
    text = "A short observation"
    for event_type, text_key in (("chat", "content"), ("observation_reaction", "text")):
        for metadata in ("25", -1, 50_001_000):
            sanitized = sanitize_activity_event(
                {
                    "type": event_type,
                    "data": {text_key: text, "original_length": metadata},
                }
            )

            assert sanitized["data"]["original_length"] == len(text)
            assert 0 <= sanitized["data"]["original_length"] <= 50_000


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


def test_activity_outbox_flush_preserves_event_enqueued_between_drain_opportunities():
    temp_dir = tempfile.mkdtemp(prefix="ru_outbox_flush_race_")
    try:
        writer = ActivityOutboxWriter(log_dir=temp_dir, flush_interval_seconds=10.0)
        writer._stop_event.set()
        writer._thread.join(timeout=1.0)

        event = {
            "type": "chat",
            "session_id": "sess_race",
            "data": {"role": "assistant", "content": "arrived during flush"},
        }
        original_drain = writer._drain_batch
        drain_calls = 0

        def drain_with_enqueue():
            nonlocal drain_calls
            drain_calls += 1
            batch = original_drain()
            if drain_calls == 1:
                assert writer.try_enqueue({**event, "event_id": "evt_between_drains"}) is True
            return batch

        writer._drain_batch = drain_with_enqueue  # type: ignore[method-assign]
        writer.flush(timeout_seconds=1.0)
        writer.close()

        outbox_file = Path(temp_dir) / "activity_outbox.jsonl"
        records = [json.loads(line) for line in outbox_file.read_text(encoding="utf-8").splitlines()]
        assert [record["event_id"] for record in records] == ["evt_between_drains"]
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
