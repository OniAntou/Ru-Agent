from __future__ import annotations

import argparse
import asyncio
import json
import os
import secrets
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any

import httpx
from websockets.asyncio.client import connect

from benchmark_aspnetcore_compat import (
    _websocket_url,
    benchmark_target,
)


def classify_route_for_safe_probe(entry: dict[str, Any]) -> str:
    if entry.get("transport") == "websocket":
        return "websocket"
    if entry.get("method") == "GET":
        return "probe"
    return "skip: mutating HTTP method"


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


_PRIVATE_METADATA_FRAGMENTS = ("[[vts:", "[[voice_pace:", "[[emo:", "[search:")


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


def _load_matrix(repo_root: Path) -> list[dict[str, Any]]:
    return json.loads(
        (repo_root / "backend-dotnet" / "public-route-contract.json").read_text(
            encoding="utf-8"
        )
    )


def _start_child(
    command: list[str],
    *,
    repo_root: Path,
    environment: dict[str, str],
    log_path: Path,
) -> subprocess.Popen[bytes]:
    log_file = log_path.open("wb")
    creation_flags = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
    try:
        return subprocess.Popen(
            command,
            cwd=repo_root,
            env=environment,
            stdout=log_file,
            stderr=subprocess.STDOUT,
            creationflags=creation_flags,
        )
    except Exception:
        log_file.close()
        raise


def _stop_child(process: subprocess.Popen[bytes]) -> bool:
    if process.poll() is not None:
        return True
    try:
        process.terminate()
        process.wait(timeout=5)
    except (OSError, subprocess.TimeoutExpired):
        if os.name == "nt":
            subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
        else:
            process.kill()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            return False
    return process.poll() is not None


def _wait_for_edge(
    base_url: str,
    token: str,
    timeout_seconds: float = 60.0,
    path: str = "/settings",
) -> int | None:
    deadline = time.monotonic() + timeout_seconds
    headers = {"Authorization": f"Bearer {token}"}
    with httpx.Client(timeout=2.0, headers=headers) as client:
        while time.monotonic() < deadline:
            try:
                response = client.get(base_url.rstrip("/") + path)
                status = response.status_code
                response.close()
                if 200 <= status < 300:
                    return status
            except httpx.HTTPError:
                pass
            time.sleep(0.25)
    return None


def _probe_http_routes(base_url: str, token: str, matrix: list[dict[str, Any]]) -> dict[str, Any]:
    headers = {"Authorization": f"Bearer {token}"}
    results: list[dict[str, Any]] = []
    with httpx.Client(timeout=15.0, headers=headers) as client:
        for entry in matrix:
            action = classify_route_for_safe_probe(entry)
            if action != "probe":
                if entry.get("transport") == "http":
                    results.append(
                        {
                            "method": entry["method"],
                            "path": entry["path"],
                            "status": "skipped",
                            "reason": action,
                        }
                    )
                continue
            started = time.perf_counter()
            try:
                response = client.get(base_url.rstrip("/") + entry["path"])
                status_code = response.status_code
                response.close()
                results.append(
                    {
                        "method": entry["method"],
                        "path": entry["path"],
                        "status": status_code,
                        "elapsed_ms": round((time.perf_counter() - started) * 1000, 3),
                        "ok": status_code < 500,
                    }
                )
            except Exception as error:
                results.append(
                    {
                        "method": entry["method"],
                        "path": entry["path"],
                        "status": "error",
                        "error_type": type(error).__name__,
                    }
                )
    failures = sum(result.get("ok") is False or result.get("status") == "error" for result in results)
    return {
        "probed": sum(result.get("status") not in {"skipped", "error"} for result in results),
        "skipped": sum(result.get("status") == "skipped" for result in results),
        "failures": failures,
        "results": results,
    }


async def _probe_chat(base_url: str, token: str) -> dict[str, Any]:
    uri = _websocket_url(base_url, "/chat", token)
    async with connect(uri, proxy=None, open_timeout=10, close_timeout=2) as socket:
        await socket.send("{")
        message = await asyncio.wait_for(socket.recv(), timeout=10)
    payload = json.loads(message)
    return {"connected": True, "event_type": payload.get("type"), "ordered": payload.get("type") == "error"}


async def _probe_synthetic_chat(base_url: str, token: str) -> dict[str, Any]:
    uri = _websocket_url(base_url, "/chat", token)
    events: list[dict[str, Any]] = []
    async with connect(uri, proxy=None, open_timeout=10, close_timeout=2) as socket:
        await socket.send(
            json.dumps(
                {"prompt": "Anh thích trả lời ngắn."},
                ensure_ascii=False,
            )
        )
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline and len(events) < 12:
            message = await asyncio.wait_for(
                socket.recv(),
                timeout=max(0.1, deadline - time.monotonic()),
            )
            if not isinstance(message, str):
                continue
            payload = json.loads(message)
            if not isinstance(payload, dict):
                continue
            events.append(payload)
            if payload.get("type") in {"done", "error"}:
                break
    return summarize_chat_events(events)


async def _probe_voice_control(base_url: str, token: str) -> dict[str, Any]:
    uri = _websocket_url(base_url, "/voice/stream", token)
    async with connect(uri, proxy=None, open_timeout=10, close_timeout=2) as socket:
        await socket.send(json.dumps({"type": "invalid"}))
        message = await asyncio.wait_for(socket.recv(), timeout=10)
    payload = json.loads(message)
    return {"connected": True, "event_type": payload.get("type"), "ordered": payload.get("type") == "failed"}


async def _probe_real_voice_audio(base_url: str, token: str) -> bool:
    uri = _websocket_url(base_url, "/voice/stream", token)
    async with connect(uri, proxy=None, open_timeout=10, close_timeout=2) as socket:
        await socket.send(json.dumps({"type": "start", "request_id": "runtime-probe", "text": "Xin chào."}))
        pending_audio = False
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            message = await asyncio.wait_for(socket.recv(), timeout=max(0.1, deadline - time.monotonic()))
            if isinstance(message, bytes):
                if pending_audio and message:
                    return True
                continue
            payload = json.loads(message)
            if payload.get("type") == "audio":
                pending_audio = True
            elif payload.get("type") in {"failed", "ended", "cancelled"}:
                return False
    return False


def _wait_for_ports_closed(ports: list[int], timeout_seconds: float = 5.0) -> bool:
    deadline = time.monotonic() + timeout_seconds
    pending = set(ports)
    while pending and time.monotonic() < deadline:
        for port in tuple(pending):
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=0.2):
                    pass
            except OSError:
                pending.remove(port)
        if pending:
            time.sleep(0.1)
    return not pending


def _serve_deterministic_worker(port: int) -> int:
    from ru_runtime.worker.v1.protocol import (
        CHAT_CHUNK,
        CHAT_DONE,
        CHAT_RESET,
        ERROR,
        MEMORY_FACTS,
        RUNTIME_STATE,
        VTS_ACTION,
        VTS_CATALOG,
    )
    from ru_runtime.worker.v1.server import WorkerHandlers, WorkerReply, serve_worker

    async def chat(_payload, _cancelled):
        yield WorkerReply(CHAT_RESET, {})
        yield WorkerReply(CHAT_CHUNK, {"text": "Em nghe đây. [[VT"})
        yield WorkerReply(
            CHAT_DONE,
            {
                "raw_reply": (
                    "Em nghe đây. [[EMO:joy]] [[VTS:vts_01]] "
                    "[[VOICE_PACE:quick]]"
                )
            },
        )

    async def memory(_payload, _cancelled):
        yield WorkerReply(MEMORY_FACTS, {"facts": []})

    async def runtime(payload, _cancelled):
        yield WorkerReply(
            RUNTIME_STATE,
            {"status": "success", "message": "Runtime loaded.", "backend": payload.get("backend")},
        )

    async def vts(payload, _cancelled):
        if payload.get("operation") == "catalog":
            yield WorkerReply(
                VTS_CATALOG,
                {
                    "model_id": "synthetic-model",
                    "model_name": "synthetic",
                    "hotkey_codes": ["vts_01"],
                    "expression_codes": [],
                },
            )
            return
        if payload.get("operation") == "action":
            yield WorkerReply(VTS_ACTION, {"status": "ok"})
            return
        yield WorkerReply(ERROR, {"code": "unsupported_operation", "retryable": False})

    async def unsupported(_payload, _cancelled):
        yield WorkerReply(ERROR, {"code": "unsupported_operation", "retryable": False})

    token = os.environ.get("RU_WORKER_TOKEN", "").strip()
    asyncio.run(
        serve_worker(
            "127.0.0.1",
            port,
            token,
            WorkerHandlers(
                chat=chat,
                memory_extract=memory,
                runtime=runtime,
                voice=unsupported,
                vts=vts,
            ),
        )
    )
    return 0


def run_harness(args: argparse.Namespace) -> dict[str, Any]:
    repo_root = args.project_root.resolve()
    token = secrets.token_hex(16)
    python_executable = repo_root / ".venv" / "Scripts" / "python.exe"
    if not python_executable.is_file():
        python_executable = Path(sys.executable)
    edge_base_url = f"http://127.0.0.1:{args.edge_port}"
    harness_script = Path(__file__).resolve()
    if args.deterministic_chat:
        worker_command = [
            str(python_executable),
            str(harness_script),
            "--serve-deterministic-worker",
            "--worker-port",
            str(args.worker_port),
        ]
    else:
        worker_command = [
            str(python_executable),
            "-m",
            "ru_runtime.worker.v1.server",
            "--host",
            "127.0.0.1",
            "--port",
            str(args.worker_port),
        ]
    edge_command = [
        "dotnet",
        "run",
        "--project",
        str(repo_root / "backend-dotnet" / "RuBackend" / "RuBackend.csproj"),
        "--no-launch-profile",
        "--",
        "--urls",
        edge_base_url,
    ]
    children: list[subprocess.Popen[bytes]] = []
    with tempfile.TemporaryDirectory(
        prefix="ru-aspnetcore-runtime-",
        ignore_cleanup_errors=True,
    ) as temporary_root:
        temp_path = Path(temporary_root)
        environment = os.environ.copy()
        environment.update(
            {
                "RU_DESKTOP_TOKEN": token,
                "RU_DATA_ROOT": str(temp_path / "data"),
                "RuBackend__BindUrl": edge_base_url,
                "NO_PROXY": "127.0.0.1,localhost",
                "no_proxy": "127.0.0.1,localhost",
            }
        )
        environment.update(build_runtime_environment(token, args.worker_port))
        try:
            children.append(
                _start_child(
                    worker_command,
                    repo_root=repo_root,
                    environment=environment,
                    log_path=temp_path / "worker.log",
                )
            )
            children.append(
                _start_child(
                    edge_command,
                    repo_root=repo_root,
                    environment=environment,
                    log_path=temp_path / "aspnetcore.log",
                )
            )
            readiness_status = _wait_for_edge(
                edge_base_url,
                token,
                path="/settings" if args.deterministic_chat else "/status",
            )
            matrix = _load_matrix(repo_root)
            result: dict[str, Any] = {
                "worker_port": args.worker_port,
                "edge_port": args.edge_port,
                "deterministic_chat": args.deterministic_chat,
                "edge_readiness_status": readiness_status,
                "http": (
                    {"probed": 1, "skipped": 0, "failures": 0, "results": []}
                    if readiness_status and args.deterministic_chat
                    else _probe_http_routes(edge_base_url, token, matrix)
                    if readiness_status
                    else {"failures": 1}
                ),
                "websockets": {},
                "voice_audio_confirmed": False,
            }
            if readiness_status:
                if args.deterministic_chat:
                    result["websockets"]["chat"] = asyncio.run(
                        _probe_synthetic_chat(edge_base_url, token)
                    )
                    result["chat_state"] = inspect_chat_state(temp_path / "data")
                else:
                    result["websockets"]["chat"] = asyncio.run(_probe_chat(edge_base_url, token))
                    result["websockets"]["voice_control"] = asyncio.run(_probe_voice_control(edge_base_url, token))
                if args.require_voice_audio and not args.deterministic_chat:
                    result["voice_audio_confirmed"] = asyncio.run(_probe_real_voice_audio(edge_base_url, token))
                if args.benchmark_samples and not args.deterministic_chat:
                    result["benchmark"] = {
                        "samples": args.benchmark_samples,
                        "aspnetcore": benchmark_target(
                            edge_base_url,
                            token,
                            args.benchmark_samples,
                        ),
                    }
            return result
        finally:
            terminated = [_stop_child(child) for child in reversed(children)]
            result_to_update = locals().get("result")
            if isinstance(result_to_update, dict):
                result_to_update["children_terminated"] = all(terminated)
                result_to_update["child_exit_codes"] = [child.poll() for child in children]
                result_to_update["ports_closed"] = _wait_for_ports_closed(
                    [args.worker_port, args.edge_port]
                )


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Run an isolated ASP.NET Core and private-runtime probe"
    )
    parser.add_argument("--project-root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--worker-port", type=int, default=8001)
    parser.add_argument("--edge-port", type=int, default=8010)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--require-voice-audio", action="store_true")
    parser.add_argument("--benchmark-samples", type=int, default=0)
    parser.add_argument("--deterministic-chat", action="store_true")
    parser.add_argument("--isolated", action="store_true")
    parser.add_argument(
        "--serve-deterministic-worker",
        action="store_true",
        help=argparse.SUPPRESS,
    )
    args = parser.parse_args()
    if args.serve_deterministic_worker:
        return _serve_deterministic_worker(args.worker_port)
    if args.benchmark_samples < 0:
        parser.error("--benchmark-samples cannot be negative")
    output = args.output or (
        args.project_root
        / "artifacts"
        / "aspnetcore"
        / ("deterministic-chat-gate.json" if args.deterministic_chat else "runtime-gate.json")
    )
    try:
        result = run_harness(args)
    except Exception as error:
        result = {"error_type": type(error).__name__, "error": redact_secrets(str(error), [])}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    failed = bool(result.get("error_type")) or result.get("edge_readiness_status") is None
    failed = failed or result.get("http", {}).get("failures", 1) > 0
    failed = failed or not result.get("children_terminated", False)
    failed = failed or not result.get("ports_closed", False)
    if args.deterministic_chat:
        chat = result.get("websockets", {}).get("chat", {})
        state = result.get("chat_state", {})
        failed = failed or not chat.get("ordered", False)
        failed = failed or not chat.get("private_metadata_absent", False)
        failed = failed or chat.get("terminal_events") != 1
        failed = failed or state.get("assistant_messages") != 1
        failed = failed or state.get("user_messages") != 1
        failed = failed or not state.get("private_metadata_absent", False)
        failed = failed or state.get("activity_events") != 2
        failed = failed or state.get("memory_active_records") != 1
    if args.require_voice_audio and not result.get("voice_audio_confirmed", False):
        failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
