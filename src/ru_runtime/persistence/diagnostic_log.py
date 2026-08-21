from __future__ import annotations

import collections
import datetime
import logging
import os
import threading
import time
import uuid
from collections.abc import Mapping
from typing import List, Optional

from ru_runtime.persistence.diagnostic_context import get_correlation_id
from ru_runtime.persistence.diagnostic_schema import (
    DiagnosticEvent,
    sanitize_diagnostic_event,
    serialize_diagnostic_event,
)
from ru_runtime.persistence.activity_outbox import get_default_log_root

_SEVERITY_RANKS = {
    "trace": 1,
    "debug": 2,
    "info": 3,
    "warning": 4,
    "error": 5,
    "critical": 6,
}


class BoundedDiagnosticQueue:
    def __init__(self, capacity: int = 1000) -> None:
        self.capacity = max(1, capacity)
        self._items: collections.deque[DiagnosticEvent] = collections.deque()
        self._lock = threading.Lock()
        self.enqueued_count = 0
        self.dropped_count = 0
        self.write_failure_count = 0

    @property
    def size(self) -> int:
        with self._lock:
            return len(self._items)

    def try_enqueue(self, event: DiagnosticEvent) -> bool:
        with self._lock:
            if len(self._items) >= self.capacity:
                candidate_rank = _SEVERITY_RANKS.get(event.level.lower(), 3)
                # Find lowest rank item in deque
                lowest_idx = -1
                lowest_rank = candidate_rank
                for idx, item in enumerate(self._items):
                    r = _SEVERITY_RANKS.get(item.level.lower(), 3)
                    if r < lowest_rank:
                        lowest_rank = r
                        lowest_idx = idx

                if lowest_idx >= 0:
                    del self._items[lowest_idx]
                    self.dropped_count += 1
                else:
                    self.dropped_count += 1
                    return False

            self._items.append(event)
            self.enqueued_count += 1
            return True

    def drain_batch(self, max_batch_size: int) -> List[DiagnosticEvent]:
        with self._lock:
            batch: List[DiagnosticEvent] = []
            while self._items and len(batch) < max_batch_size:
                batch.append(self._items.popleft())
            return batch

    def requeue_front(self, batch: List[DiagnosticEvent]) -> None:
        if not batch:
            return
        with self._lock:
            for event in reversed(batch):
                if len(self._items) >= self.capacity:
                    self._items.popleft()
                    self.dropped_count += 1
                self._items.appendleft(event)

    def record_write_failure(self) -> None:
        with self._lock:
            self.write_failure_count += 1


class DiagnosticLogWriter:
    def __init__(
        self,
        log_dir: Optional[str] = None,
        prefix: str = "worker-",
        capacity: int = 1000,
        batch_size: int = 64,
        flush_interval_seconds: float = 0.2,
    ) -> None:
        self.log_dir = str(log_dir) if log_dir else str(get_default_log_root())
        self.prefix = prefix
        self.batch_size = max(1, min(512, batch_size))
        self.flush_interval_seconds = max(0.01, flush_interval_seconds)
        self.queue = BoundedDiagnosticQueue(capacity=capacity)

        self._stop_event = threading.Event()
        self._write_lock = threading.Lock()
        self._thread = threading.Thread(target=self._worker_loop, daemon=True)
        self._thread.start()

    def try_enqueue(self, event: DiagnosticEvent) -> bool:
        try:
            safe_event = sanitize_diagnostic_event(event)
        except Exception:
            self.queue.record_write_failure()
            return False
        return self.queue.try_enqueue(safe_event)

    def flush(self, timeout_seconds: float = 2.0) -> None:
        deadline = time.time() + timeout_seconds
        while time.time() < deadline:
            batch = self.queue.drain_batch(self.batch_size)
            if not batch:
                break
            if not self._write_batch(batch):
                break

    def close(self) -> None:
        self._stop_event.set()
        self._thread.join(timeout=2.0)
        # Flush remaining
        batch = self.queue.drain_batch(self.batch_size)
        while batch:
            if not self._write_batch(batch):
                break
            batch = self.queue.drain_batch(self.batch_size)

    def _worker_loop(self) -> None:
        while not self._stop_event.is_set():
            try:
                batch = self.queue.drain_batch(self.batch_size)
                if batch:
                    if not self._write_batch(batch):
                        self._stop_event.wait(timeout=self.flush_interval_seconds)
                else:
                    self._stop_event.wait(timeout=self.flush_interval_seconds)
            except Exception:
                self.queue.record_write_failure()

    def _write_batch(self, batch: List[DiagnosticEvent]) -> bool:
        if not batch:
            return True
        with self._write_lock:
            try:
                os.makedirs(self.log_dir, exist_ok=True)
                # Group by date
                by_date: dict[str, list[DiagnosticEvent]] = collections.defaultdict(list)
                for evt in batch:
                    dt = datetime.datetime.fromtimestamp(evt.ts, datetime.timezone.utc)
                    date_str = dt.strftime("%Y-%m-%d")
                    by_date[date_str].append(evt)

                for date_str, evts in by_date.items():
                    file_path = os.path.join(self.log_dir, f"{self.prefix}{date_str}.jsonl")
                    lines = [serialize_diagnostic_event(e) + "\n" for e in evts]
                    with open(file_path, "a", encoding="utf-8") as f:
                        f.writelines(lines)
                return True
            except Exception:
                self.queue.record_write_failure()
                self.queue.requeue_front(batch)
                return False


class DiagnosticLoggingHandler(logging.Handler):
    """Persist safe structured fields from a Python LogRecord.

    The formatted message, arguments, exception text, and traceback are
    intentionally never read. A legacy/free-form record becomes an
    unclassified event with only bounded metadata.
    """

    def __init__(self, writer: Optional[DiagnosticLogWriter] = None) -> None:
        super().__init__(level=logging.NOTSET)
        self.writer = writer or get_diagnostic_log_writer()

    def emit(self, record: logging.LogRecord) -> None:
        try:
            raw_fields = getattr(record, "ru_fields", {})
            fields: Mapping[str, object] = raw_fields if isinstance(raw_fields, Mapping) else {}
            error_type = "unknown"
            if record.exc_info and record.exc_info[0] is not None:
                name = record.exc_info[0].__name__.casefold()
                if "timeout" in name:
                    error_type = "timeout"
                elif "permission" in name or "unauthorized" in name:
                    error_type = "unauthorized"
                elif "connection" in name or "network" in name:
                    error_type = "network"
                elif "io" in name or "file" in name:
                    error_type = "io"
                elif "cancel" in name:
                    error_type = "cancelled"

            duration = fields.get("duration_ms")
            if isinstance(duration, bool) or not isinstance(duration, (int, float)):
                duration = None

            event = DiagnosticEvent(
                event_id=f"evt_{uuid.uuid4().hex}",
                ts=time.time(),
                iso=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                level=record.levelname.casefold(),
                component=record.name,
                event_code=str(getattr(record, "ru_event_code", "diagnostic_unclassified")),
                correlation_id=get_correlation_id(),
                duration_ms=float(duration) if duration is not None else None,
                status=fields.get("status") if isinstance(fields.get("status"), str) else None,
                error_code=fields.get("error_code") if isinstance(fields.get("error_code"), str) else None,
                exception_kind=error_type if record.exc_info else (
                    fields.get("exception_kind")
                    if isinstance(fields.get("exception_kind"), str)
                    else None
                ),
            )
            self.writer.try_enqueue(event)
        except Exception:
            # Logging must never recursively fail the operation that emitted it.
            return


def install_diagnostic_logging_handler(
    writer: Optional[DiagnosticLogWriter] = None,
) -> DiagnosticLoggingHandler:
    root = logging.getLogger()
    for handler in root.handlers:
        if isinstance(handler, DiagnosticLoggingHandler):
            return handler

    handler = DiagnosticLoggingHandler(writer)
    root.addHandler(handler)
    return handler


def remove_diagnostic_logging_handler() -> None:
    root = logging.getLogger()
    for handler in list(root.handlers):
        if isinstance(handler, DiagnosticLoggingHandler):
            root.removeHandler(handler)
            handler.close()


_global_diagnostic_writer: Optional[DiagnosticLogWriter] = None
_global_writer_lock = threading.Lock()


def get_diagnostic_log_writer() -> DiagnosticLogWriter:
    global _global_diagnostic_writer
    writer = _global_diagnostic_writer
    if writer is not None:
        return writer
    with _global_writer_lock:
        if _global_diagnostic_writer is None:
            _global_diagnostic_writer = DiagnosticLogWriter()
        return _global_diagnostic_writer


def reset_global_diagnostic_writer() -> None:
    global _global_diagnostic_writer
    with _global_writer_lock:
        if _global_diagnostic_writer is not None:
            _global_diagnostic_writer.close()
            _global_diagnostic_writer = None
