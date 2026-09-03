"""Append-only activity log for Ru runtime.

Routing activity events through the safe activity outbox.
"""

from __future__ import annotations

import json
import logging
import threading
import time
from datetime import UTC, datetime
from pathlib import Path

from ru_runtime.persistence.activity_outbox import (
    get_activity_outbox_writer,
    get_outbox_file,
)

logger = logging.getLogger("ru_runtime.persistence.activity_log")

MAX_ENTRIES = 5000
MAX_RECORD_BYTES = 256_000
_log_lock = threading.Lock()


def get_activity_log_file() -> Path:
    return get_outbox_file()


def _now_iso() -> str:
    return datetime.now(UTC).isoformat()


def append(event: dict) -> None:
    """Publish a sanitized activity event to the worker outbox."""
    event.setdefault("ts", time.time())
    event.setdefault("iso", _now_iso())
    writer = get_activity_outbox_writer()
    writer.try_enqueue(event)


def clear() -> None:
    """Clear outbox log."""
    with _log_lock:
        try:
            get_activity_outbox_writer().flush(timeout_seconds=0.5)
            get_activity_log_file().unlink(missing_ok=True)
        except OSError as e:
            logger.warning("activity_log clear failed: %s", e)


def read(limit: int = 200) -> list[dict]:
    limit = max(1, min(int(limit), 500))
    writer = get_activity_outbox_writer()
    writer.flush(timeout_seconds=1.0)

    target = get_activity_log_file()
    if not target.is_file():
        return []
    events: list[dict] = []
    with _log_lock:
        try:
            with target.open("r", encoding="utf-8") as fh:
                while True:
                    line = fh.readline(MAX_RECORD_BYTES + 1)
                    if not line:
                        break
                    if len(line) > MAX_RECORD_BYTES:
                        # Drain the remainder of an overlong physical record,
                        # then continue with the next newline-delimited event.
                        while not line.endswith(("\n", "\r")):
                            line = fh.readline(MAX_RECORD_BYTES + 1)
                            if not line:
                                break
                        continue
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        events.append(json.loads(line))
                    except json.JSONDecodeError:
                        continue
        except OSError as e:
            logger.warning("activity_log read failed: %s", e)
            return []
    events.sort(key=lambda e: e.get("ts", 0.0))
    return events[-limit:]
