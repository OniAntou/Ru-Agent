from __future__ import annotations

import json
import logging
import os
import shutil
import tempfile
import time
from ru_runtime.persistence.diagnostic_log import (
    DiagnosticLogWriter,
    BoundedDiagnosticQueue,
    DiagnosticLoggingHandler,
)
from ru_runtime.persistence.diagnostic_schema import DiagnosticEvent


def test_python_diagnostic_log_writer_flushes_batches_and_rotates_by_date():
    temp_dir = tempfile.mkdtemp(prefix="ru_diag_py_")
    try:
        writer = DiagnosticLogWriter(
            log_dir=temp_dir,
            prefix="worker-",
            capacity=100,
            batch_size=64,
            flush_interval_seconds=0.05,
        )

        evt1 = DiagnosticEvent(
            event_id="evt_1",
            ts=1720000000.1,
            iso="2026-08-20T00:00:00.100Z",
            level="info",
            component="worker.runtime",
            event_code="worker_startup",
        )
        evt2 = DiagnosticEvent(
            event_id="evt_2",
            ts=1720000000.2,
            iso="2026-08-20T00:00:00.200Z",
            level="error",
            component="worker.voice",
            event_code="voice_unavailable",
        )

        assert writer.try_enqueue(evt1) is True
        assert writer.try_enqueue(evt2) is True

        writer.flush(timeout_seconds=2.0)
        writer.close()

        files = [f for f in os.listdir(temp_dir) if f.startswith("worker-") and f.endswith(".jsonl")]
        assert len(files) == 1

        with open(os.path.join(temp_dir, files[0]), "r", encoding="utf-8") as fp:
            lines = fp.readlines()

        assert len(lines) == 2
        assert "worker_startup" in lines[0]
        assert "voice_unavailable" in lines[1]
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_python_queue_drops_low_severity_on_overflow():
    queue = BoundedDiagnosticQueue(capacity=3)

    e1 = DiagnosticEvent("e1", 1, "2026-08-20T00:00:00Z", "info", "worker", "c1")
    e2 = DiagnosticEvent("e2", 2, "2026-08-20T00:00:00Z", "debug", "worker", "c2")
    e3 = DiagnosticEvent("e3", 3, "2026-08-20T00:00:00Z", "trace", "worker", "c3")

    assert queue.try_enqueue(e1) is True
    assert queue.try_enqueue(e2) is True
    assert queue.try_enqueue(e3) is True

    # Error should cause drop of low severity item
    e4 = DiagnosticEvent("e4", 4, "2026-08-20T00:00:00Z", "error", "worker", "c4")
    assert queue.try_enqueue(e4) is True

    assert queue.dropped_count >= 1
    assert queue.size == 3


def test_diagnostic_logging_handler_drops_formatted_message_and_traceback():
    temp_dir = tempfile.mkdtemp(prefix="ru_diag_handler_")
    try:
        writer = DiagnosticLogWriter(log_dir=temp_dir, prefix="worker-")
        handler = DiagnosticLoggingHandler(writer)
        logger = logging.getLogger("test.worker.voice")
        try:
            raise ValueError("secret prompt and Bearer token")
        except ValueError:
            record = logger.makeRecord(
                logger.name,
                logging.ERROR,
                __file__,
                1,
                "raw message %s",
                ("secret",),
                __import__("sys").exc_info(),
            )
        record.ru_event_code = "voice_failed"
        record.ru_fields = {
            "status": "failed",
            "error_code": "worker_unavailable",
            "duration_ms": 12.345,
        }

        handler.emit(record)
        writer.flush(timeout_seconds=2.0)
        writer.close()

        files = [f for f in os.listdir(temp_dir) if f.startswith("worker-")]
        assert len(files) == 1
        payload = open(os.path.join(temp_dir, files[0]), encoding="utf-8").read()
        assert "voice_failed" in payload
        assert "raw message" not in payload
        assert "secret" not in payload
        assert "Bearer" not in payload
        assert "ValueError" not in payload
        assert "12.35" in payload
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_python_diagnostic_writer_requeues_failed_batches():
    temp_file = tempfile.NamedTemporaryFile(prefix="ru_diag_blocked_", delete=False)
    temp_file.close()
    try:
        writer = DiagnosticLogWriter(log_dir=temp_file.name, prefix="worker-")
        event = DiagnosticEvent(
            event_id="evt_retry",
            ts=time.time(),
            iso="2026-08-20T00:00:00.000Z",
            level="error",
            component="worker.voice",
            event_code="voice_failed",
        )
        assert writer.try_enqueue(event) is True

        writer.flush(timeout_seconds=0.1)

        assert writer.queue.write_failure_count >= 1
        assert writer.queue.size >= 1
        writer.close()
    finally:
        os.unlink(temp_file.name)
