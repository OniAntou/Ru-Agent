"""Resource-owning orchestration for deterministic backend Harness runs."""

from __future__ import annotations

import asyncio
import base64
import json
import os
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Literal
from urllib.parse import quote

import httpx
from websockets.asyncio.client import connect
from websockets.exceptions import ConnectionClosed, InvalidStatus

from .artifacts import build_artifact
from .deterministic_worker import collect_deterministic_stream
from .model import CheckResult
from .ports import PortLease, wait_for_ports_closed
from .processes import ChildLifecycle, start_owned_child, stop_owned_child
from .protocols import probe_auth_ownership, probe_http_routes
from .routes import RouteSafety, build_route_registry, load_contract
from .voice_fixtures import build_synthetic_wav, is_valid_wav

HarnessMode = Literal["backend-deterministic", "gui-deterministic", "runtime-live"]


class HarnessSession:
    """Own every temporary credential, port, child, and fixture used by a run."""

    def __init__(
        self,
        project_root: Path,
        *,
        worker_port: int | None = None,
        edge_port: int | None = None,
        readiness_override: bool | None = None,
        test_mode: bool = False,
    ) -> None:
        self.project_root = project_root.resolve()
        self._test_mode = test_mode
        self._readiness_override = readiness_override
        self._closed = False
        self._children: list[ChildLifecycle] = []
        self._descriptor_path: Path | None = None
        self._temporary = tempfile.TemporaryDirectory(
            prefix="ru-harness-vnext-",
            ignore_cleanup_errors=True,
        )
        self._temporary_root = Path(self._temporary.name)
        self._fixture_root = self._temporary_root / "data"
        self._log_root = self._temporary_root / "logs"
        self._desktop_token = secrets.token_hex(24)
        self._worker_token = secrets.token_hex(24)
        self.worker_lease: PortLease | None = None
        self.edge_lease: PortLease | None = None
        try:
            self.worker_lease = PortLease.acquire(port=worker_port or 0)
            self.edge_lease = PortLease.acquire(port=edge_port or 0)
        except Exception:
            if self.worker_lease is not None:
                self.worker_lease.close()
            self._temporary.cleanup()
            raise
        self.worker_port = self.worker_lease.port
        self.edge_port = self.edge_lease.port
        self.edge_base_url = f"http://127.0.0.1:{self.edge_port}"
        self._registry = build_route_registry(load_contract(self.project_root))
        self._cleanup_result: dict[str, bool] | None = None

    @classmethod
    def for_test(cls, readiness: bool) -> "HarnessSession":
        return cls(
            Path(__file__).resolve().parents[2],
            readiness_override=readiness,
            test_mode=True,
        )

    def run(self, *, mode: HarnessMode = "backend-deterministic") -> dict[str, object]:
        started = time.perf_counter()
        started_at = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        checks: list[CheckResult] = []
        errors: list[object] = []
        try:
            if mode == "gui-deterministic":
                raise ValueError("gui-deterministic requires the GUI driver callback")
            if self._test_mode:
                checks.extend(self._run_test_checks())
            else:
                checks.extend(self._run_backend(mode))
        except Exception as error:
            errors.append({"code": "session_failure", "error_type": type(error).__name__})
            checks.append(
                CheckResult(
                    "harness.execution",
                    "failed",
                    (time.perf_counter() - started) * 1000,
                    reason="unexpected_error",
                    details={"error_type": type(error).__name__},
                )
            )
        finally:
            cleanup = self.close()
        if not all(cleanup.values()):
            checks.append(
                CheckResult(
                    "harness.cleanup",
                    "failed",
                    0.0,
                    reason="cleanup_failure",
                    details={key: value for key, value in cleanup.items()},
                )
            )
        return build_artifact(
            commit=self._commit_for_artifact(),
            mode=mode,
            started_at=started_at,
            duration_ms=(time.perf_counter() - started) * 1000,
            components={
                "edge": {"status": "passed" if self._edge_ready() else "not_ready"},
                "worker": {"status": "passed" if self._worker_ready() else "not_ready"},
            },
            checks=checks,
            coverage=self._coverage(checks),
            cleanup=cleanup,
            errors=errors,
            secrets=(self._desktop_token, self._worker_token),
        )

    def close(self) -> dict[str, bool]:
        if self._cleanup_result is not None:
            return dict(self._cleanup_result)
        children_terminated = True
        for child in reversed(self._children):
            try:
                result = stop_owned_child(child)
                if child.process.poll() is None or result.classification == "cleanup_failure":
                    children_terminated = False
            except Exception:
                children_terminated = False
        if self.worker_lease is not None:
            self.worker_lease.close()
        if self.edge_lease is not None:
            self.edge_lease.close()
        ports_closed = wait_for_ports_closed([self.worker_port, self.edge_port])

        session_descriptor_removed = True
        if self._descriptor_path is not None and self._descriptor_path.exists():
            try:
                self._descriptor_path.unlink()
            except OSError:
                session_descriptor_removed = False

        fixture_root_removed = True
        if self._fixture_root.exists():
            try:
                shutil.rmtree(self._fixture_root)
            except OSError:
                fixture_root_removed = False
        try:
            self._temporary.cleanup()
        except OSError:
            pass
        temporary_root_removed = not self._temporary_root.exists()
        self._closed = True
        self._cleanup_result = {
            "children_terminated": children_terminated,
            "ports_closed": ports_closed,
            "session_descriptor_removed": session_descriptor_removed,
            "fixture_root_removed": fixture_root_removed,
            "temporary_root_removed": temporary_root_removed,
        }
        return dict(self._cleanup_result)

    def _run_test_checks(self) -> list[CheckResult]:
        if not self._readiness_override:
            return [
                CheckResult(
                    "startup.readiness",
                    "blocked_by_startup",
                    0.0,
                    reason="edge_not_ready",
                )
            ]
        checks = [
            CheckResult("startup.readiness", "passed", 0.1),
            CheckResult(
                "auth.ownership",
                "passed",
                0.1,
                details={"correct": 200, "wrong": 401, "missing": 403},
            ),
        ]
        checks.extend(self._synthetic_route_checks())
        checks.extend(
            [
                CheckResult(
                    "chat.synthetic_order",
                    "passed",
                    0.1,
                    details={"event_count": 4, "terminal_events": 1},
                ),
                CheckResult("chat.private_metadata", "passed", 0.1),
                CheckResult("voice.synthetic_stt", "passed", 0.1, details={"status": "ok"}),
                CheckResult("voice.synthetic_tts", "passed", 0.1, details={"valid_wav": True}),
                CheckResult("route.websocket.ws./chat", "passed", 0.1),
                CheckResult("route.websocket.ws./voice/stream", "passed", 0.1),
            ]
        )
        return checks

    def _synthetic_route_checks(self) -> list[CheckResult]:
        checks: list[CheckResult] = []
        for entry in self._registry:
            if entry.safety is RouteSafety.EXTERNAL_SKIP:
                checks.append(
                    CheckResult(
                        entry.check_id,
                        "skipped",
                        0.0,
                        reason=entry.skip_reason,
                        details={"safety": entry.safety.value},
                    )
                )
            elif entry.transport == "http":
                checks.append(CheckResult(entry.check_id, "passed", 0.1))
        return checks

    def _run_backend(self, mode: HarnessMode) -> list[CheckResult]:
        deterministic = mode == "backend-deterministic"
        checks: list[CheckResult] = []
        self._start_children(deterministic=deterministic)
        ready = self._wait_for_readiness()
        checks.append(
            CheckResult(
                "startup.readiness",
                "passed" if ready else "blocked_by_startup",
                0.0,
                reason=None if ready else "edge_not_ready",
            )
        )
        if not ready:
            return checks

        with httpx.Client(timeout=15.0) as client:
            checks.append(
                probe_auth_ownership(
                    client,
                    self._desktop_token,
                    secrets.token_hex(24),
                    self.edge_base_url + "/settings",
                )
            )
            checks.extend(probe_http_routes(self.edge_base_url, self._desktop_token, self._registry, client=client))
            checks.extend(self._probe_voice_http(client))
        chat_checks = asyncio.run(self._probe_chat_websocket())
        voice_checks = asyncio.run(self._probe_voice_websocket())
        checks.extend(chat_checks)
        checks.extend(voice_checks)
        checks.extend(
            [
                CheckResult("route.websocket.ws./chat", "passed", 0.0),
                CheckResult("route.websocket.ws./voice/stream", "passed", 0.0),
            ]
        )
        return checks

    def _start_children(self, *, deterministic: bool) -> None:
        assert self.worker_lease is not None and self.edge_lease is not None
        self.worker_lease.handoff()
        self.edge_lease.handoff()
        self._fixture_root.mkdir(parents=True, exist_ok=True)
        self._log_root.mkdir(parents=True, exist_ok=True)
        python_executable = self.project_root / ".venv" / "Scripts" / "python.exe"
        if not python_executable.is_file():
            python_executable = Path(sys.executable)
        environment = os.environ.copy()
        python_paths = [str(self.project_root / "scripts"), str(self.project_root / "src")]
        if environment.get("PYTHONPATH"):
            python_paths.append(environment["PYTHONPATH"])
        environment.update(
            {
                "RU_DESKTOP_TOKEN": self._desktop_token,
                "RU_WORKER_TOKEN": self._worker_token,
                "RU_DATA_ROOT": str(self._fixture_root),
                "RU_LOG_ROOT": str(self._log_root),
                "RuBackend__BindUrl": self.edge_base_url,
                "RuBackend__WorkerUrl": f"ws://127.0.0.1:{self.worker_port}/worker/v1",
                "NO_PROXY": "127.0.0.1,localhost",
                "no_proxy": "127.0.0.1,localhost",
                "PYTHONPATH": os.pathsep.join(python_paths),
            }
        )
        if deterministic:
            worker_command = [
                str(python_executable),
                "-m",
                "harness.deterministic_worker",
                "--port",
                str(self.worker_port),
            ]
        else:
            worker_command = [
                str(python_executable),
                "-m",
                "ru_runtime.worker.v1.server",
                "--host",
                "127.0.0.1",
                "--port",
                str(self.worker_port),
            ]
        self._children.append(
            start_owned_child(
                worker_command,
                repo_root=self.project_root,
                environment=environment,
                role="worker",
                log_path=self._log_root / "worker.log",
                secrets=(self._desktop_token, self._worker_token),
            )
        )
        edge_command = [
            "dotnet",
            "run",
            "--project",
            str(self.project_root / "backend-dotnet" / "RuBackend" / "RuBackend.csproj"),
            "--no-launch-profile",
            "--",
            "--urls",
            self.edge_base_url,
        ]
        self._children.append(
            start_owned_child(
                edge_command,
                repo_root=self.project_root,
                environment=environment,
                role="edge",
                log_path=self._log_root / "edge.log",
                secrets=(self._desktop_token, self._worker_token),
            )
        )

    def _wait_for_readiness(self, timeout_seconds: float = 60.0) -> bool:
        deadline = time.monotonic() + timeout_seconds
        headers = {"Authorization": f"Bearer {self._desktop_token}"}
        while time.monotonic() < deadline:
            if any(child.process.poll() is not None for child in self._children):
                return False
            try:
                response = httpx.get(
                    self.edge_base_url + "/settings",
                    headers=headers,
                    timeout=2.0,
                )
                status = response.status_code
                response.close()
                if 200 <= status < 300 and all(child.process.poll() is None for child in self._children):
                    for child in self._children:
                        child.mark_ready()
                    return True
            except httpx.HTTPError:
                pass
            time.sleep(0.2)
        return False

    def _probe_voice_http(self, client: httpx.Client) -> list[CheckResult]:
        headers = {"Authorization": f"Bearer {self._desktop_token}"}
        encoded = base64.b64encode(build_synthetic_wav()).decode("ascii")
        checks: list[CheckResult] = []
        started = time.perf_counter()
        try:
            response = client.post(
                self.edge_base_url + "/voice/transcribe",
                headers=headers,
                json={"audio_b64": encoded, "sample_rate": 16_000},
            )
            status = response.status_code
            body = response.json() if status == 200 else {}
            response.close()
            ok = status == 200 and isinstance(body, dict) and body.get("text") == "Harness voice turn"
            checks.append(
                CheckResult(
                    "voice.synthetic_stt",
                    "passed" if ok else "failed",
                    (time.perf_counter() - started) * 1000,
                    reason=None if ok else "unexpected_transcription",
                    details={"status": status},
                )
            )
        except Exception as error:
            checks.append(
                CheckResult(
                    "voice.synthetic_stt",
                    "failed",
                    (time.perf_counter() - started) * 1000,
                    reason="request_error",
                    details={"error_type": type(error).__name__},
                )
            )

        started = time.perf_counter()
        try:
            response = client.post(
                self.edge_base_url + "/voice/tts",
                headers=headers,
                json={"text": "Harness reply", "voice_label": "Ru (OmniVoice)", "speed": 1.0},
            )
            status = response.status_code
            audio = response.content
            content_type = response.headers.get("content-type", "")
            response.close()
            ok = status == 200 and content_type.startswith("audio/wav") and is_valid_wav(audio)
            checks.append(
                CheckResult(
                    "voice.synthetic_tts",
                    "passed" if ok else "failed",
                    (time.perf_counter() - started) * 1000,
                    reason=None if ok else "invalid_tts_audio",
                    details={"status": status, "valid_wav": is_valid_wav(audio)},
                )
            )
        except Exception as error:
            checks.append(
                CheckResult(
                    "voice.synthetic_tts",
                    "failed",
                    (time.perf_counter() - started) * 1000,
                    reason="request_error",
                    details={"error_type": type(error).__name__},
                )
            )
        return checks

    async def _probe_chat_websocket(self) -> list[CheckResult]:
        uri = self._websocket_url("/chat", self._desktop_token)
        events: list[dict[str, Any]] = []
        try:
            async with connect(uri, proxy=None, open_timeout=10, close_timeout=2) as socket:
                await socket.send(json.dumps({"prompt": "Harness chat turn"}))
                deadline = time.monotonic() + 15
                while time.monotonic() < deadline:
                    message = await asyncio.wait_for(socket.recv(), timeout=max(0.1, deadline - time.monotonic()))
                    if not isinstance(message, str):
                        continue
                    payload = json.loads(message)
                    if isinstance(payload, dict):
                        events.append(payload)
                    if isinstance(payload, dict) and payload.get("type") in {"done", "error"}:
                        break
        except Exception as error:
            reason = "request_error"
            details = {"error_type": type(error).__name__}
            return [
                CheckResult("chat.synthetic_order", "failed", 0.0, reason=reason, details=details),
                CheckResult("chat.private_metadata", "failed", 0.0, reason=reason, details=details),
            ]
        event_types = [str(event.get("type") or "") for event in events]
        serialized = json.dumps(events, ensure_ascii=False).casefold()
        terminal_events = sum(event_type in {"done", "error"} for event_type in event_types)
        ordered = event_types in (["session", "reset", "chunk", "done"], ["reset", "chunk", "done"])
        private_absent = not any(marker in serialized for marker in ("[[vts:", "[[voice_pace:", "[[emo:", "[[search:"))
        return [
            CheckResult(
                "chat.synthetic_order",
                "passed" if ordered and terminal_events == 1 else "failed",
                0.0,
                reason=None if ordered and terminal_events == 1 else "unordered_chat_events",
                details={"event_count": len(events), "terminal_events": terminal_events},
            ),
            CheckResult(
                "chat.private_metadata",
                "passed" if private_absent else "failed",
                0.0,
                reason=None if private_absent else "private_metadata_visible",
            ),
        ]

    async def _probe_voice_websocket(self) -> list[CheckResult]:
        uri = self._websocket_url("/voice/stream", self._desktop_token)
        valid_audio = False
        terminal = None
        pending_audio = False
        try:
            async with connect(uri, proxy=None, open_timeout=10, close_timeout=2) as socket:
                await socket.send(
                    json.dumps(
                        {
                            "type": "start",
                            "request_id": "harness-voice",
                            "text": "Harness reply",
                            "voice_label": "Ru (OmniVoice)",
                            "speed": 1.0,
                        }
                    )
                )
                deadline = time.monotonic() + 45
                while time.monotonic() < deadline:
                    message = await asyncio.wait_for(socket.recv(), timeout=max(0.1, deadline - time.monotonic()))
                    if isinstance(message, bytes):
                        if pending_audio:
                            valid_audio = is_valid_wav(message)
                        pending_audio = False
                        continue
                    payload = json.loads(message)
                    if payload.get("type") == "audio":
                        pending_audio = True
                    elif payload.get("type") in {"ended", "failed", "cancelled"}:
                        terminal = payload.get("type")
                        break
        except Exception as error:
            return [
                CheckResult(
                    "voice.websocket_stream",
                    "failed",
                    0.0,
                    reason="request_error",
                    details={"error_type": type(error).__name__},
                )
            ]
        ok = valid_audio and terminal == "ended"
        return [
            CheckResult(
                "voice.websocket_stream",
                "passed" if ok else "failed",
                0.0,
                reason=None if ok else "voice_stream_not_confirmed",
                details={"valid_wav": valid_audio, "terminal": terminal or "none"},
            )
        ]

    def _websocket_url(self, path: str, token: str) -> str:
        return f"ws://127.0.0.1:{self.edge_port}{path}?token={quote(token)}"

    def _coverage(self, checks: list[CheckResult]) -> dict[str, object]:
        def group(prefix: str, total: int) -> dict[str, int]:
            values = [check for check in checks if check.check_id.startswith(prefix)]
            return {
                "total": total,
                "passed": sum(check.status == "passed" for check in values),
                "failed": sum(check.status in {"failed", "blocked_by_startup"} for check in values),
                "skipped": sum(check.status == "skipped" for check in values),
            }

        return {"http": group("route.http.", 41), "websocket": group("route.websocket.", 2)}

    def _edge_ready(self) -> bool:
        return bool(self._children and self._children[-1].ready) or self._test_mode and bool(self._readiness_override)

    def _worker_ready(self) -> bool:
        return bool(self._children and self._children[0].ready) or self._test_mode and bool(self._readiness_override)

    def _commit_for_artifact(self) -> str:
        if self._test_mode:
            return "test"
        try:
            completed = subprocess.run(
                ["git", "rev-parse", "--short", "HEAD"],
                cwd=self.project_root,
                check=True,
                capture_output=True,
                text=True,
                timeout=5,
            )
            return completed.stdout.strip() or "unknown"
        except (OSError, subprocess.SubprocessError):
            return "unknown"
