from __future__ import annotations

import json
import sqlite3

from aspnetcore_compatibility_harness import (
    classify_route_for_safe_probe,
    inspect_agent_state,
    is_valid_wav,
    redact_secrets,
    safe_probe_status_ok,
    summarize_agent_events,
)
from benchmark_aspnetcore_compat import percentile, summarize_samples
from harness.routes import RouteSafety


def test_percentile_uses_linear_interpolation() -> None:
    assert percentile([10.0, 20.0, 30.0, 40.0], 50) == 25.0
    assert percentile([10.0, 20.0, 30.0, 40.0], 95) == 38.5


def test_empty_summary_is_explicit_and_failure_count_is_preserved() -> None:
    assert summarize_samples([], failures=2) == {
        "sample_count": 0,
        "failures": 2,
        "p50_ms": None,
        "p95_ms": None,
    }


def test_safe_probe_classifies_mutating_routes() -> None:
    assert classify_route_for_safe_probe(
        {"method": "POST", "transport": "http", "path": "/settings"}
    ) == RouteSafety.FIXTURE_MUTATION
    assert classify_route_for_safe_probe(
        {"method": "GET", "transport": "http", "path": "/status"}
    ) == "probe"
    assert classify_route_for_safe_probe(
        {"method": "WS", "transport": "websocket", "path": "/voice/stream"}
    ) == "websocket"


def test_safe_probe_fails_closed_for_auth_routing_and_validation_errors() -> None:
    entry = {"method": "GET", "transport": "http", "path": "/status"}

    assert safe_probe_status_ok(entry, 200) is True
    assert safe_probe_status_ok(entry, 401) is False
    assert safe_probe_status_ok(entry, 404) is False
    assert safe_probe_status_ok(entry, 422) is False
    assert safe_probe_status_ok(entry, 500) is False


def test_safe_probe_allows_explicitly_offline_optional_devices_only() -> None:
    assert safe_probe_status_ok(
        {"path": "/voice/status"},
        503,
    ) is True
    assert safe_probe_status_ok({"path": "/settings"}, 503) is False
    assert safe_probe_status_ok(
        {"path": "/status", "classification": "runtime-worker"},
        503,
        allow_worker_unavailable=True,
    ) is True


def test_wav_probe_rejects_non_audio_bytes_and_accepts_playable_wav() -> None:
    assert is_valid_wav(b"RIFF-not-a-wave") is False

    import io
    import wave

    output = io.BytesIO()
    with wave.open(output, "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(24_000)
        audio.writeframes(b"\x00\x00" * 24)

    assert is_valid_wav(output.getvalue()) is True


def test_redaction_removes_secrets_without_retaining_body_text() -> None:
    text = redact_secrets(
        "Authorization: Bearer local-secret; response body: private text",
        ["local-secret", "private text"],
    )

    assert "local-secret" not in text
    assert "private text" not in text
    assert "[REDACTED]" in text


def test_agent_summary_records_order_and_privacy_without_retaining_content() -> None:
    events = [
        {"type": "session", "session_id": "private-session"},
        {"type": "reset"},
        {"type": "chunk", "content": "Em nghe đây. [[VTS:private-code]]"},
        {"type": "done", "content": "Em nghe đây."},
    ]

    summary = summarize_agent_events(events)

    assert summary == {
        "event_types": ["session", "reset", "chunk", "done"],
        "event_count": 4,
        "session_events": 1,
        "terminal_events": 1,
        "ordered": True,
        "private_metadata_absent": False,
    }
    serialized = json.dumps(summary)
    assert "private-session" not in serialized
    assert "private-code" not in serialized
    assert "Em nghe" not in serialized


def test_agent_state_inspection_returns_only_counts_and_privacy_flags(tmp_path) -> None:
    data_root = tmp_path / "data"
    sessions = data_root / "sessions"
    sessions.mkdir(parents=True)
    (sessions / "session-1.json").write_text(
        json.dumps(
            {
                "messages": [
                    {"role": "user", "content": "private prompt"},
                    {"role": "ai", "content": "clean reply"},
                ]
            }
        ),
        encoding="utf-8",
    )
    (data_root / "activity_log.jsonl").write_text(
        json.dumps({"data": {"role": "user", "content": "private prompt"}})
        + "\n"
        + json.dumps({"data": {"role": "assistant", "content": "clean reply"}})
        + "\n",
        encoding="utf-8",
    )
    with sqlite3.connect(data_root / "memory.db") as database:
        database.execute("CREATE TABLE memory_records (status TEXT)")
        database.execute("INSERT INTO memory_records(status) VALUES ('active')")

    summary = inspect_agent_state(data_root)

    assert summary == {
        "session_files": 1,
        "session_messages": 2,
        "user_messages": 1,
        "assistant_messages": 1,
        "activity_events": 2,
        "memory_active_records": 1,
        "private_metadata_absent": True,
    }
    serialized = json.dumps(summary)
    assert "private prompt" not in serialized
    assert "clean reply" not in serialized
