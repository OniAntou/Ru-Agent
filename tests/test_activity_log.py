import threading
import time

import pytest

from ru_runtime.persistence.activity_log import append, clear, read
from ru_runtime.persistence.activity_outbox import reset_global_outbox_writer


@pytest.fixture(autouse=True)
def isolated_log(monkeypatch, tmp_path):
    reset_global_outbox_writer()
    monkeypatch.setenv("RU_LOG_ROOT", str(tmp_path))
    yield
    reset_global_outbox_writer()


def test_append_and_read_roundtrip():
    append({"ts": 1000.0, "iso": "2026-08-05T10:00:00", "type": "chat", "session_id": "s1", "data": {"role": "user", "content": "hi"}})
    events = read()
    assert len(events) == 1
    assert events[0]["type"] == "chat"
    assert "content" not in events[0]["data"]
    assert events[0]["data"]["preview"] == "hi"
    assert events[0]["data"]["original_length"] == 2


def test_append_default_timestamp_is_unix_epoch():
    before = time.time()
    append({"type": "chat", "session_id": None, "data": {"role": "user", "content": "hi"}})
    after = time.time()
    ts = read()[0]["ts"]
    assert before - 1 <= ts <= after + 1
    assert ts > 1_000_000_000


def test_read_respects_limit():
    for i in range(5):
        append({"ts": float(i), "iso": f"2026-08-05T10:00:0{i}", "type": "chat", "session_id": None, "data": {"role": "assistant", "content": f"msg{i}"}})
    assert len(read(limit=2)) == 2
    assert len(read(limit=10)) == 5


def test_read_sorted_by_timestamp():
    append({"ts": 2.0, "iso": "...", "type": "chat", "session_id": None, "data": {"role": "assistant", "content": "b"}})
    append({"ts": 1.0, "iso": "...", "type": "chat", "session_id": None, "data": {"role": "assistant", "content": "a"}})
    assert [e["data"]["preview"] for e in read()] == ["a", "b"]


def test_append_thread_safe():
    errors = []

    def worker(offset):
        try:
            for i in range(20):
                append({"ts": float(offset + i), "iso": "...", "type": "chat", "session_id": None, "data": {"role": "assistant", "content": f"t{offset}-{i}"}})
        except Exception as e:
            errors.append(e)

    threads = [threading.Thread(target=worker, args=(i * 100,)) for i in range(4)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert not errors
    assert len(read()) == 80


def test_clear_empties_log():
    append({"type": "chat", "session_id": None, "data": {"role": "user", "content": "hi"}})
    assert len(read()) == 1
    clear()
    assert read() == []
    append({"type": "chat", "session_id": None, "data": {"role": "user", "content": "after"}})
    assert [e["data"]["preview"] for e in read()] == ["after"]


def test_clear_missing_file_is_noop():
    clear()
    assert read() == []
