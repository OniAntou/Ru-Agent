from __future__ import annotations

import json
import re
import time
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Optional

ALLOWED_LEVELS = {"trace", "debug", "info", "warning", "error", "critical"}
ALLOWED_STATUSES = {"started", "succeeded", "failed", "skipped", "timeout", "cancelled", "dropped"}
ALLOWED_EXCEPTION_KINDS = {"timeout", "network", "io", "invalid_argument", "unauthorized", "cancelled", "unknown"}
ALLOWED_COMPONENTS = {
    "host", "host.http", "host.ws", "host.memory", "host.conversation", "host.speech", "host.vts",
    "worker", "worker.core", "worker.voice", "worker.vts", "worker.observation", "worker.generation", "worker.memory", "worker.runtime",
    "gui", "gui.main", "gui.voice", "gui.vts",
}
ALLOWED_EVENT_CODES = {
    "diagnostic_unclassified", "worker_startup", "worker_shutdown", "worker_unavailable", "worker_timeout",
    "voice_started", "voice_completed", "voice_failed", "voice_unavailable",
    "vts_action_started", "vts_action_completed", "vts_action_failed", "vts_unavailable",
    "chat_started", "chat_completed", "chat_failed",
    "stt_started", "stt_completed", "stt_failed",
    "observation_started", "observation_completed", "observation_failed",
    "memory_queued", "memory_saved", "memory_failed",
}

SAFE_CODE_PATTERN = re.compile(r"^[a-z0-9_.-]{1,64}$")
SAFE_COMPONENT_PATTERN = re.compile(r"^[a-z0-9_.-]{1,64}$")
VALID_CORRELATION_PATTERN = re.compile(r"^[a-zA-Z0-9_-]{8,64}$")


@dataclass(frozen=True)
class DiagnosticEvent:
    event_id: str
    ts: float
    iso: str
    level: str
    component: str
    event_code: str
    correlation_id: Optional[str] = None
    duration_ms: Optional[float] = None
    status: Optional[str] = None
    error_code: Optional[str] = None
    exception_kind: Optional[str] = None


def sanitize_diagnostic_event(event: DiagnosticEvent) -> DiagnosticEvent:
    now_utc = datetime.now(timezone.utc)
    raw_event_id = (event.event_id or "").strip().lower()
    event_id = raw_event_id if SAFE_CODE_PATTERN.match(raw_event_id) else f"evt_{uuid.uuid4().hex}"

    ts = float(event.ts) if event.ts and event.ts > 0 else time.time()
    iso = (event.iso or "").strip()
    if not iso or "T" not in iso:
        iso = now_utc.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"

    raw_level = (event.level or "").strip().lower()
    level = raw_level if raw_level in ALLOWED_LEVELS else "info"

    raw_component = (event.component or "").strip().lower()
    component = raw_component if SAFE_COMPONENT_PATTERN.match(raw_component) else "worker"

    raw_code = (event.event_code or "").strip().lower()
    event_code = raw_code if SAFE_CODE_PATTERN.match(raw_code) else "diagnostic_unclassified"

    correlation_id: Optional[str] = None
    if event.correlation_id:
        raw_cid = event.correlation_id.strip()
        if VALID_CORRELATION_PATTERN.match(raw_cid):
            correlation_id = raw_cid
        else:
            correlation_id = f"c_{uuid.uuid4().hex}"

    duration_ms: Optional[float] = None
    if event.duration_ms is not None and 0 <= event.duration_ms <= 86400000:
        duration_ms = round(float(event.duration_ms), 2)

    status: Optional[str] = None
    if event.status:
        raw_status = event.status.strip().lower()
        if raw_status in ALLOWED_STATUSES:
            status = raw_status

    error_code: Optional[str] = None
    if event.error_code:
        raw_err = event.error_code.strip().lower()
        if SAFE_CODE_PATTERN.match(raw_err):
            error_code = raw_err

    exception_kind: Optional[str] = None
    if event.exception_kind:
        raw_exc = event.exception_kind.strip().lower()
        exception_kind = raw_exc if raw_exc in ALLOWED_EXCEPTION_KINDS else "unknown"

    return DiagnosticEvent(
        event_id=event_id,
        ts=ts,
        iso=iso,
        level=level,
        component=component,
        event_code=event_code,
        correlation_id=correlation_id,
        duration_ms=duration_ms,
        status=status,
        error_code=error_code,
        exception_kind=exception_kind,
    )


def serialize_diagnostic_event(event: DiagnosticEvent) -> str:
    sanitized = sanitize_diagnostic_event(event)
    data: dict[str, Any] = {
        "event_id": sanitized.event_id,
        "ts": sanitized.ts,
        "iso": sanitized.iso,
        "level": sanitized.level,
        "component": sanitized.component,
        "event_code": sanitized.event_code,
    }
    if sanitized.correlation_id is not None:
        data["correlation_id"] = sanitized.correlation_id
    if sanitized.duration_ms is not None:
        data["duration_ms"] = sanitized.duration_ms
    if sanitized.status is not None:
        data["status"] = sanitized.status
    if sanitized.error_code is not None:
        data["error_code"] = sanitized.error_code
    if sanitized.exception_kind is not None:
        data["exception_kind"] = sanitized.exception_kind

    return json.dumps(data, ensure_ascii=False, separators=(",", ":"))


def deserialize_diagnostic_event(raw_json: str) -> Optional[DiagnosticEvent]:
    if not raw_json or not raw_json.strip():
        return None
    try:
        parsed = json.loads(raw_json)
        if not isinstance(parsed, dict):
            return None
        raw_event = DiagnosticEvent(
            event_id=str(parsed.get("event_id") or ""),
            ts=float(parsed.get("ts") or 0),
            iso=str(parsed.get("iso") or ""),
            level=str(parsed.get("level") or ""),
            component=str(parsed.get("component") or ""),
            event_code=str(parsed.get("event_code") or ""),
            correlation_id=parsed.get("correlation_id"),
            duration_ms=parsed.get("duration_ms"),
            status=parsed.get("status"),
            error_code=parsed.get("error_code"),
            exception_kind=parsed.get("exception_kind"),
        )
        return sanitize_diagnostic_event(raw_event)
    except Exception:
        return None
