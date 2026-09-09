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
    reset_global_outbox_writer,
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
    event_copy = dict(event)
    event_copy.setdefault("ts", time.time())
    event_copy.setdefault("iso", _now_iso())
    writer = get_activity_outbox_writer()
    writer.try_enqueue(event_copy)


def clear() -> None:
    """Clear outbox log."""
    with _log_lock:
        try:
            # Close + reset the writer under lock so the background worker
            # cannot recreate the file with a stale in-flight batch after unlink.
            try:
                get_activity_outbox_writer().close()
            except Exception as exc:
                logger.warning("activity_log writer close failed: %s", exc)
            reset_global_outbox_writer()
            get_activity_log_file().unlink(missing_ok=True)
        except OSError as e:
            logger.warning("activity_log clear failed: %s", e)


def _sort_key_ts(event: dict) -> float:
    ts = event.get("ts", 0.0)
    if isinstance(ts, bool):
        return 0.0
    if isinstance(ts, (int, float)):
        return float(ts)
    if isinstance(ts, str):
        try:
            return float(ts.strip() or 0.0)
        except ValueError:
            return 0.0
    return 0.0


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
                        obj = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if not isinstance(obj, dict):
                        continue
                    events.append(obj)
        except OSError as e:
            logger.warning("activity_log read failed: %s", e)
            return []
    events.sort(key=_sort_key_ts)
    return events[-limit:]
