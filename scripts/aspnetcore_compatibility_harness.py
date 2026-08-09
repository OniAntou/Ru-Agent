from __future__ import annotations

import argparse
import asyncio
import json
import os
import secrets
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any

import httpx
from websockets.asyncio.client import connect

from benchmark_aspnetcore_compat import _websocket_url


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


def _load_matrix(repo_root: Path) -> list[dict[str, Any]]:
    return json.loads(
        (repo_root / "backend-dotnet" / "compatibility-route-matrix.json").read_text(
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


def _wait_for_edge(base_url: str, token: str, timeout_seconds: float = 60.0) -> int | None:
    deadline = time.monotonic() + timeout_seconds
    headers = {"Authorization": f"Bearer {token}"}
    with httpx.Client(timeout=2.0, headers=headers) as client:
        while time.monotonic() < deadline:
            try:
                response = client.get(base_url.rstrip("/") + "/settings")
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
        await socket.send(json.dumps({"type": "start", "request_id": "compatibility-probe", "text": "Xin chào."}))
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


def run_harness(args: argparse.Namespace) -> dict[str, Any]:
    repo_root = args.project_root.resolve()
    token = secrets.token_hex(16)
    python_executable = repo_root / ".venv" / "Scripts" / "python.exe"
    if not python_executable.is_file():
        python_executable = Path(sys.executable)
    edge_base_url = f"http://127.0.0.1:{args.edge_port}"
    worker_command = [
        str(python_executable),
        "-m",
        "mini_gpt.server",
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
    with tempfile.TemporaryDirectory(prefix="ru-aspnetcore-compat-") as temporary_root:
        temp_path = Path(temporary_root)
        environment = os.environ.copy()
        environment.update(
            {
                "RU_DESKTOP_TOKEN": token,
                "RU_DATA_ROOT": str(temp_path / "data"),
                "RuBackend__BindUrl": edge_base_url,
                "RuBackend__UpstreamUrl": f"http://127.0.0.1:{args.worker_port}",
                "NO_PROXY": "127.0.0.1,localhost",
                "no_proxy": "127.0.0.1,localhost",
            }
        )
        try:
            children.append(
                _start_child(
                    worker_command,
                    repo_root=repo_root,
                    environment=environment,
                    log_path=temp_path / "fastapi.log",
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
            readiness_status = _wait_for_edge(edge_base_url, token)
            matrix = _load_matrix(repo_root)
            result: dict[str, Any] = {
                "worker_port": args.worker_port,
                "edge_port": args.edge_port,
                "edge_readiness_status": readiness_status,
                "http": _probe_http_routes(edge_base_url, token, matrix) if readiness_status else {"failures": 1},
                "websockets": {},
                "voice_audio_confirmed": False,
            }
            if readiness_status:
                result["websockets"]["chat"] = asyncio.run(_probe_chat(edge_base_url, token))
                result["websockets"]["voice_control"] = asyncio.run(_probe_voice_control(edge_base_url, token))
                if args.require_voice_audio:
                    result["voice_audio_confirmed"] = asyncio.run(_probe_real_voice_audio(edge_base_url, token))
            return result
        finally:
            terminated = [_stop_child(child) for child in reversed(children)]
            result_to_update = locals().get("result")
            if isinstance(result_to_update, dict):
                result_to_update["children_terminated"] = all(terminated)
                result_to_update["child_exit_codes"] = [child.poll() for child in children]


def main() -> int:
    parser = argparse.ArgumentParser(description="Run an isolated FastAPI-to-ASP.NET Core compatibility probe")
    parser.add_argument("--project-root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--worker-port", type=int, default=8001)
    parser.add_argument("--edge-port", type=int, default=8010)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--require-voice-audio", action="store_true")
    args = parser.parse_args()
    try:
        result = run_harness(args)
    except Exception as error:
        result = {"error_type": type(error).__name__, "error": redact_secrets(str(error), [])}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    failed = bool(result.get("error_type")) or result.get("edge_readiness_status") is None
    failed = failed or result.get("http", {}).get("failures", 1) > 0
    failed = failed or not result.get("children_terminated", False)
    if args.require_voice_audio and not result.get("voice_audio_confirmed", False):
        failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
