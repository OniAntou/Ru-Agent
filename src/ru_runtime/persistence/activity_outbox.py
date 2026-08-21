from __future__ import annotations

import collections
import json
import os
import re
import threading
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, List, Optional, Tuple

_URL_PATTERN = re.compile(r"https?://\S+", re.IGNORECASE)
_BEARER_PATTERN = re.compile(r"Bearer\s+\S+", re.IGNORECASE)
_KEY_PATTERN = re.compile(r"(?:sk-|key=)[a-zA-Z0-9_\-]{8,}", re.IGNORECASE)
_CONTROL_MARKER_PATTERN = re.compile(r"\[\[.*?\]\]", re.IGNORECASE)
_PATH_PATTERN = re.compile(r"(?:[a-zA-Z]:\\|\b/)[a-zA-Z0-9_\-\\/.]+", re.IGNORECASE)


def get_default_log_root() -> Path:
    explicit = os.environ.get("RU_LOG_ROOT", "").strip()
    if explicit:
        return Path(explicit)
    local_app_data = os.environ.get("LOCALAPPDATA", "").strip()
    if not local_app_data:
        local_app_data = os.environ.get("USERPROFILE", "").strip()
    if not local_app_data:
        local_app_data = os.getcwd()
    return Path(local_app_data) / "Ru" / "logs"


def get_outbox_file() -> Path:
    return get_default_log_root() / "activity_outbox.jsonl"


DEFAULT_OUTBOX_FILE = get_outbox_file()


def format_activity_preview(text: Optional[str], max_length: int = 160) -> Tuple[str, int, bool]:
    if not text:
        return "", 0, False

    orig_len = len(text)
    redacted = False
    cleaned = text

    # Strip control markers
    if "[[" in cleaned:
        cleaned, n_ctrl = _CONTROL_MARKER_PATTERN.subn("", cleaned)
        if n_ctrl > 0:
            redacted = True

    # Redact secrets
    if "sk-" in cleaned or "key=" in cleaned.lower():
        cleaned, n_key = _KEY_PATTERN.subn("[REDACTED_KEY]", cleaned)
        if n_key > 0:
            redacted = True

    if "bearer" in cleaned.lower():
        cleaned, n_bearer = _BEARER_PATTERN.subn("[REDACTED_TOKEN]", cleaned)
        if n_bearer > 0:
            redacted = True

    # Redact URLs
    if "http://" in cleaned.lower() or "https://" in cleaned.lower():
        cleaned, n_url = _URL_PATTERN.subn("[URL]", cleaned)
        if n_url > 0:
            redacted = True

    # Redact absolute paths
    if ":\\" in cleaned or (cleaned.startswith("/") and len(cleaned) > 2):
        cleaned, n_path = _PATH_PATTERN.subn("[PATH]", cleaned)
        if n_path > 0:
            redacted = True

    # Normalize whitespace
    normalized = " ".join(cleaned.split()).strip()

    if len(normalized) > max_length:
        cut = max(0, max_length - 3)
        preview = normalized[:cut].rstrip() + "..."
        redacted = True
    else:
        preview = normalized

    return preview, orig_len, redacted


def sanitize_activity_event(event: dict) -> dict:
    evt = dict(event)

    ts = float(evt.get("ts") or 0.0)
    if ts <= 0:
        ts = time.time()

    event_id = str(evt.get("event_id") or "").strip()
    if not event_id or len(event_id) < 4:
        event_id = f"evt_{int(ts * 1000)}_{uuid.uuid4().hex[:8]}"

    iso = str(evt.get("iso") or "").strip()
    if not iso or "T" not in iso:
        iso = datetime.fromtimestamp(ts, tz=timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"

    event_type = str(evt.get("type") or "chat").strip().lower()
    session_id = evt.get("session_id")
    if session_id is not None:
        session_id = str(session_id).strip()

    raw_data = dict(evt.get("data") or {})

    sanitized_data: dict[str, Any] = {}
    if event_type == "chat":
        role = str(raw_data.get("role") or "assistant").strip().lower()
        if role not in {"user", "assistant", "system"}:
            role = "assistant"

        content = raw_data.get("content") or raw_data.get("preview") or ""
        preview, orig_len, redacted = format_activity_preview(str(content))

        sanitized_data["role"] = role
        sanitized_data["preview"] = preview
        sanitized_data["original_length"] = int(raw_data.get("original_length") or orig_len)
        sanitized_data["redacted"] = bool(raw_data.get("redacted") or redacted)

    elif event_type == "emotion":
        emotion = str(raw_data.get("emotion") or raw_data.get("name") or "neutral").strip().lower()
        status = str(raw_data.get("status") or "succeeded").strip().lower()
        sanitized_data["emotion"] = emotion
        sanitized_data["status"] = status

    elif event_type == "vts_action":
        category = str(raw_data.get("category") or raw_data.get("action") or "vts_action").strip().lower()
        status = str(raw_data.get("status") or "succeeded").strip().lower()
        sanitized_data["category"] = category
        sanitized_data["status"] = status

    elif event_type == "observation_reaction":
        if "text" in raw_data and raw_data["text"] is not None:
            text = str(raw_data["text"]).strip()
            sanitized_data["preview"] = text[:120]
            sanitized_data["original_length"] = len(text)
            sanitized_data["redacted"] = False
        if "scene" in raw_data and raw_data["scene"]:
            sanitized_data["scene"] = str(raw_data["scene"])[:100]
        if "event" in raw_data and raw_data["event"]:
            sanitized_data["event"] = str(raw_data["event"])[:100]
        if "observation_id" in raw_data and raw_data["observation_id"]:
            sanitized_data["observation_id"] = str(raw_data["observation_id"])[:64]

    else:
        allowed_scalar_keys = {"status", "category", "duration_ms", "error_code", "observation_id"}
        for k, v in raw_data.items():
            k_str = str(k).lower().strip()
            if k_str in allowed_scalar_keys:
                if isinstance(v, (int, float)):
                    sanitized_data[k_str] = round(float(v), 2)
                elif isinstance(v, bool):
                    sanitized_data[k_str] = v
                elif isinstance(v, str):
                    sanitized_data[k_str] = v[:100]

    return {
        "event_id": event_id,
        "ts": ts,
        "iso": iso,
        "type": event_type,
        "session_id": session_id,
        "data": sanitized_data,
    }


class ActivityOutboxWriter:
    def __init__(
        self,
        log_dir: Optional[str] = None,
        capacity: int = 1000,
        batch_size: int = 64,
        flush_interval_seconds: float = 0.2,
    ) -> None:
        self._custom_log_dir = Path(log_dir) if log_dir else None
        self.capacity = max(1, capacity)
        self.batch_size = max(1, min(512, batch_size))
        self.flush_interval_seconds = max(0.01, flush_interval_seconds)

        self._queue: collections.deque[dict] = collections.deque()
        self._lock = threading.Lock()
        self._write_lock = threading.Lock()
        self._stop_event = threading.Event()
        self._dropped_count = 0
        self._write_failure_count = 0
        self._thread = threading.Thread(target=self._worker_loop, daemon=True)
        self._thread.start()

    @property
    def dropped_count(self) -> int:
        with self._lock:
            return self._dropped_count

    @property
    def write_failure_count(self) -> int:
        with self._lock:
            return self._write_failure_count

    @property
    def outbox_file(self) -> Path:
        if self._custom_log_dir:
            return self._custom_log_dir / "activity_outbox.jsonl"
        return get_outbox_file()

    def try_enqueue(self, event: dict) -> bool:
        try:
            sanitized = sanitize_activity_event(event)
        except Exception:
            with self._lock:
                self._write_failure_count += 1
            return False
        with self._lock:
            if len(self._queue) >= self.capacity:
                self._queue.popleft()  # FIFO drop oldest on overflow
                self._dropped_count += 1
            self._queue.append(sanitized)
            return True

    def flush(self, timeout_seconds: float = 2.0) -> None:
        deadline = time.time() + timeout_seconds
        while time.time() < deadline:
            batch = self._drain_batch()
            if not batch:
                # Give worker thread a moment if it's currently writing
                with self._write_lock:
                    pass
                if not self._drain_batch():
                    break
            if not self._write_batch(batch):
                break

    def close(self) -> None:
        self._stop_event.set()
        self._thread.join(timeout=2.0)
        batch = self._drain_batch()
        while batch:
            if not self._write_batch(batch):
                break
            batch = self._drain_batch()

    def _drain_batch(self) -> List[dict]:
        with self._lock:
            batch: List[dict] = []
            while self._queue and len(batch) < self.batch_size:
                batch.append(self._queue.popleft())
            return batch

    def _worker_loop(self) -> None:
        while not self._stop_event.is_set():
            try:
                batch = self._drain_batch()
                if batch:
                    if not self._write_batch(batch):
                        self._stop_event.wait(timeout=self.flush_interval_seconds)
                else:
                    self._stop_event.wait(timeout=self.flush_interval_seconds)
            except Exception:
                self._record_write_failure()

    def _write_batch(self, batch: List[dict]) -> bool:
        if not batch:
            return True
        with self._write_lock:
            try:
                target = self.outbox_file
                target.parent.mkdir(parents=True, exist_ok=True)
                lines = [json.dumps(e, ensure_ascii=False) + "\n" for e in batch]
                with target.open("a", encoding="utf-8") as f:
                    f.writelines(lines)
                return True
            except Exception:
                self._record_write_failure()
                self._requeue_failed_batch(batch)
                return False

    def _record_write_failure(self) -> None:
        with self._lock:
            self._write_failure_count += 1

    def _requeue_failed_batch(self, batch: List[dict]) -> None:
        with self._lock:
            for event in reversed(batch):
                if len(self._queue) >= self.capacity:
                    self._queue.popleft()
                    self._dropped_count += 1
                self._queue.appendleft(event)


_global_outbox_writer: Optional[ActivityOutboxWriter] = None
_global_lock = threading.Lock()


def get_activity_outbox_writer() -> ActivityOutboxWriter:
    global _global_outbox_writer
    writer = _global_outbox_writer
    if writer is not None:
        return writer
    with _global_lock:
        if _global_outbox_writer is None:
            _global_outbox_writer = ActivityOutboxWriter()
        return _global_outbox_writer


def reset_global_outbox_writer() -> None:
    global _global_outbox_writer
    with _global_lock:
        if _global_outbox_writer is not None:
            try:
                _global_outbox_writer.close()
            except Exception:
                pass
            _global_outbox_writer = None
