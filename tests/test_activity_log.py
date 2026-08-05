import json
import threading
from pathlib import Path

import pytest

from mini_gpt.activity_log import LOG_FILE, append, read


@pytest.fixture(autouse=True)
def isolated_log(monkeypatch, tmp_path):
    monkeypatch.setattr("mini_gpt.activity_log.LOG_FILE", tmp_path / "activity_log.jsonl")


def test_append_and_read_roundtrip():
    append({"ts": 1000.0, "iso": "2026-08-05T10:00:00", "type": "chat", "session_id": "s1", "data": {"role": "user", "content": "hi"}})
    events = read()
    assert len(events) == 1
    assert events[0]["type"] == "chat"
    assert events[0]["data"]["content"] == "hi"


def test_read_respects_limit():
    for i in range(5):
        append({"ts": float(i), "iso": f"2026-08-05T10:00:0{i}", "type": "chat", "session_id": None, "data": {"role": "assistant", "content": f"msg{i}"}})
    assert len(read(limit=2)) == 2
    assert len(read(limit=10)) == 5


def test_read_sorted_by_timestamp():
    append({"ts": 2.0, "iso": "...", "type": "chat", "session_id": None, "data": {"role": "assistant", "content": "b"}})
    append({"ts": 1.0, "iso": "...", "type": "chat", "session_id": None, "data": {"role": "assistant", "content": "a"}})
    assert [e["data"]["content"] for e in read()] == ["a", "b"]


def test_append_thread_safe():
    errors = []

    def worker(offset):
        try:
            for i in range(20):
                append({"ts": float(offset + i), "iso": "...", "type": "chat", "session_id": None, "data": {"role": "assistant", "content": f"t{offset}-{i}"}})
        except Exception as e:
            errors.append(e)

    threads = [threading.Thread(target=worker, args=(i * 100,)) for i in range(4)]
    for t in threads: t.start()
    for t in threads: t.join()
    assert not errors
    assert len(read()) == 80
