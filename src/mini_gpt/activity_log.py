"""Append-only activity log for Ru runtime.

Mỗi dòng là 1 JSON object (JSONL) với schema:
  {"ts": float, "iso": str, "type": "chat"|"emotion"|"vts_action", "session_id": str|None, "data": dict}

File được giới hạn tối đa 5000 entries (xoá entry cũ nhất khi vượt).
"""

from __future__ import annotations

import json
import logging
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

logger = logging.getLogger("mini_gpt.activity_log")

LOG_FILE = Path.home() / ".minigpt" / "activity_log.jsonl"
MAX_ENTRIES = 5000
_log_lock = threading.Lock()


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def append(event: dict) -> None:
    event.setdefault("ts", time.monotonic())
    event.setdefault("iso", _now_iso())
    with _log_lock:
        try:
            line = json.dumps(event, ensure_ascii=False)
            with LOG_FILE.open("a", encoding="utf-8") as fh:
                fh.write(line + "\n")
            _maybe_trim()
        except OSError as e:
            logger.warning("activity_log append failed: %s", e)


def read(limit: int = 200) -> list[dict]:
    limit = max(1, min(int(limit), 500))
    if not LOG_FILE.is_file():
        return []
    events: list[dict] = []
    with _log_lock:
        try:
            with LOG_FILE.open("r", encoding="utf-8") as fh:
                for line in fh:
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


def _maybe_trim() -> None:
    if not LOG_FILE.is_file():
        return
    try:
        with LOG_FILE.open("r", encoding="utf-8") as fh:
            lines = fh.readlines()
        if len(lines) <= MAX_ENTRIES:
            return
        keep = lines[-MAX_ENTRIES:]
        with LOG_FILE.open("w", encoding="utf-8") as fh:
            fh.writelines(keep)
    except OSError as e:
        logger.warning("activity_log trim failed: %s", e)
