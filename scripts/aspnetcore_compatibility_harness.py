"""Stable CLI compatibility wrapper for the Ru Harness vNext."""

from __future__ import annotations

import argparse
import io
import json
import sqlite3
import wave
from pathlib import Path
from typing import Any

from harness.routes import RouteSafety
from harness.session import HarnessSession


def classify_route_for_safe_probe(entry: dict[str, Any]) -> str | RouteSafety:
    if entry.get("transport") == "websocket":
        return "websocket"
    if str(entry.get("path") or "").startswith("/observation/"):
        return RouteSafety.EXTERNAL_SKIP
    if entry.get("method") == "GET":
        return "probe"
    return RouteSafety.FIXTURE_MUTATION


_OPTIONAL_UNAVAILABLE_STATUSES: dict[str, frozenset[int]] = {
    "/observation/vision-config": frozenset({503}),
    "/observation/status": frozenset({503}),
    "/voice/status": frozenset({503}),
    "/vts/mouse-look": frozenset({503}),
    "/vts/overlay": frozenset({503}),
    "/vts/status": frozenset({503}),
    "/vts/config": frozenset({503}),
}


def safe_probe_status_ok(
    entry: dict[str, Any],
    status_code: int,
    *,
    allow_worker_unavailable: bool = False,
) -> bool:
    if 200 <= status_code < 300:
        return True
    if status_code in _OPTIONAL_UNAVAILABLE_STATUSES.get(
        str(entry.get("path") or ""), frozenset()
    ):
        return True
    return (
        allow_worker_unavailable
        and entry.get("classification") == "runtime-worker"
        and status_code == 503
    )


def is_valid_wav(payload: bytes) -> bool:
    """Legacy helper: accept any non-empty playable WAV for existing tooling."""

    try:
        with wave.open(io.BytesIO(payload), "rb") as audio:
            return (
                audio.getnchannels() > 0
                and audio.getsampwidth() > 0
                and audio.getframerate() > 0
                and audio.getnframes() > 0
            )
    except (OSError, EOFError, wave.Error):
        return False


def redact_secrets(text: str, secrets_to_redact: list[str]) -> str:
    redacted = text
    for secret in secrets_to_redact:
        if secret:
            redacted = redacted.replace(secret, "[REDACTED]")
    return redacted


def build_runtime_environment(worker_token: str, worker_port: int) -> dict[str, str]:
    return {
        "RU_WORKER_TOKEN": worker_token,
        "RuBackend__WorkerUrl": f"ws://127.0.0.1:{worker_port}/worker/v1",
    }


_PRIVATE_METADATA_FRAGMENTS = ("[[vts:", "[[voice_pace:", "[[emo:", "[[search:")


def _contains_private_metadata(value: object) -> bool:
    if isinstance(value, str):
        folded = value.casefold()
        return any(fragment in folded for fragment in _PRIVATE_METADATA_FRAGMENTS)
    if isinstance(value, dict):
        return any(_contains_private_metadata(item) for item in value.values())
    if isinstance(value, list):
        return any(_contains_private_metadata(item) for item in value)
    return False


def summarize_chat_events(events: list[dict[str, Any]]) -> dict[str, Any]:
    event_types = [str(event.get("type") or "") for event in events]
    expected_orders = (
        ["session", "reset", "chunk", "done"],
        ["reset", "chunk", "done"],
    )
    return {
        "event_types": event_types,
        "event_count": len(event_types),
        "session_events": event_types.count("session"),
        "terminal_events": sum(value in {"done", "error"} for value in event_types),
        "ordered": event_types in expected_orders,
        "private_metadata_absent": not _contains_private_metadata(events),
    }


def inspect_chat_state(data_root: Path) -> dict[str, Any]:
    session_files = sorted((data_root / "sessions").glob("*.json"))
    messages: list[dict[str, Any]] = []
    for path in session_files:
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        stored = payload.get("messages") if isinstance(payload, dict) else None
        if isinstance(stored, list):
            messages.extend(item for item in stored if isinstance(item, dict))

    activity: list[dict[str, Any]] = []
    activity_path = data_root / "activity_log.jsonl"
    if activity_path.is_file():
        try:
            lines = activity_path.read_text(encoding="utf-8").splitlines()
        except OSError:
            lines = []
        for line in lines:
            try:
                item = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(item, dict):
                activity.append(item)

    active_memories = 0
    memory_path = data_root / "memory.db"
    if memory_path.is_file():
        try:
            with sqlite3.connect(memory_path) as database:
                tables = {
                    str(row[0])
                    for row in database.execute(
                        "SELECT name FROM sqlite_master WHERE type = 'table'"
                    )
                }
                table = "memories" if "memories" in tables else "memory_records"
                if table in tables:
                    active_memories = int(
                        database.execute(
                            f"SELECT COUNT(*) FROM {table} WHERE status = 'active'"
                        ).fetchone()[0]
                    )
        except (OSError, sqlite3.Error, TypeError, ValueError):
            active_memories = 0

    return {
        "session_files": len(session_files),
        "session_messages": len(messages),
        "user_messages": sum(item.get("role") == "user" for item in messages),
        "assistant_messages": sum(item.get("role") == "ai" for item in messages),
        "activity_events": len(activity),
        "memory_active_records": active_memories,
        "private_metadata_absent": not _contains_private_metadata(messages + activity),
    }


def _default_output(project_root: Path, mode: str) -> Path:
    filename = {
        "backend-deterministic": "ru-harness-vnext.json",
        "gui-deterministic": "ru-harness-gui.json",
        "runtime-live": "runtime-gate.json",
    }[mode]
    return project_root / "artifacts" / "aspnetcore" / filename


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Run an isolated Ru Harness session")
    parser.add_argument("--project-root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--worker-port", type=int)
    parser.add_argument("--edge-port", type=int)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--app-path", "--app", type=Path)
    parser.add_argument("--driver-project", type=Path)
    parser.add_argument(
        "--scenario",
        choices=("turn", "stop", "cancel", "stale-session"),
        default="turn",
    )
    parser.add_argument("--timeout-seconds", type=float, default=180.0)
    parser.add_argument("--require-voice-audio", action="store_true")
    parser.add_argument("--benchmark-samples", type=int, default=0)
    parser.add_argument("--deterministic-chat", action="store_true")
    parser.add_argument(
        "--mode",
        choices=("backend-deterministic", "gui-deterministic", "runtime-live"),
        default="runtime-live",
    )
    parser.add_argument(
        "--serve-deterministic-worker",
        action="store_true",
        help=argparse.SUPPRESS,
    )
    args = parser.parse_args(argv)
    if args.benchmark_samples < 0:
        parser.error("--benchmark-samples cannot be negative")
    if args.timeout_seconds <= 0 or args.timeout_seconds > 300:
        parser.error("--timeout-seconds must be between 0 and 300")
    mode = "backend-deterministic" if args.deterministic_chat else args.mode
    if args.require_voice_audio and mode != "runtime-live":
        parser.error("--require-voice-audio is only valid with --mode runtime-live")
    if args.benchmark_samples and mode != "runtime-live":
        parser.error("--benchmark-samples is only valid with --mode runtime-live")
    if args.serve_deterministic_worker:
        from harness.deterministic_worker import main as deterministic_worker_main

        port = args.worker_port or 8005
        return deterministic_worker_main(["--port", str(port)])

    project_root = args.project_root.resolve()
    output = args.output or _default_output(project_root, mode)
    try:
        session = HarnessSession(
            project_root,
            worker_port=args.worker_port,
            edge_port=args.edge_port,
        )
        artifact = session.run(
            mode=mode,
            app_path=args.app_path,
            driver_project=args.driver_project,
            scenario=args.scenario,
            timeout_seconds=args.timeout_seconds,
        )
    except Exception as error:
        artifact = {
            "schema_version": 2,
            "harness_version": "vnext-1",
            "mode": mode,
            "summary": {"status": "failed", "failed_checks": 1},
            "errors": [{"code": "startup_failure", "error_type": type(error).__name__}],
        }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(artifact, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    return 0 if artifact.get("summary", {}).get("status") == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
