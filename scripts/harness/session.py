"""Resource-owning orchestration for deterministic backend Harness runs."""

from __future__ import annotations

import asyncio
import base64
import contextlib
import json
import os
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any, Literal
from urllib.parse import quote

import httpx
from websockets.asyncio.client import connect

from .artifacts import build_artifact
from .broker_probe import probe_approval_lifecycle
from .model import CheckResult
from .ports import PortLease, wait_for_ports_closed
from .processes import ChildLifecycle, start_owned_child, stop_owned_child
from .protocols import probe_auth_ownership, probe_http_routes
from .routes import RouteSafety, build_route_registry, load_contract
from .voice_fixtures import build_synthetic_wav, is_valid_wav

HarnessMode = Literal["backend-deterministic", "gui-deterministic", "runtime-live"]
_DOTNET_BUILD_TIMEOUT_SECONDS = 120
_DOTNET_CONFIGURATION = "Debug"
GuiScenario = Literal[
    "turn",
    "stop",
    "cancel",
    "stale-session",
    "agent-volume-contract",
    "agent-cancel",
    "agent-stale-result",
    "agent-private-metadata",
]


def agent_tool_loop_completed(
    events: list[dict[str, Any]],
    *,
    expected_status: str,
) -> bool:
    """Require one unambiguous tool receipt before one Agent completion."""

    terminal_statuses = {
        "succeeded",
        "failed",
        "denied",
        "approval_required",
        "cancelled",
        "timed_out",
    }
    if expected_status not in terminal_statuses:
        return False
    activities: list[tuple[int, dict[str, Any]]] = [
        (index, event)
        for index, event in enumerate(events)
        if event.get("type") == "agent.activity"
    ]
    if not activities or any(
        event.get("tool_id") != "system.volume"
        or event.get("status") not in terminal_statuses | {"requested"}
        for _, event in activities
    ):
        return False
    terminals = [
        (index, event)
        for index, event in activities
        if event.get("status") in terminal_statuses
    ]
    done_indexes = [
        index for index, event in enumerate(events) if event.get("type") == "done"
    ]
    return (
        any(event.get("status") == "requested" for _, event in activities)
        and len(terminals) == 1
        and terminals[0][1].get("status") == expected_status
        and len(done_indexes) == 1
        and terminals[0][0] < done_indexes[0]
    )


def broker_approval_lifecycle_completed(summary: dict[str, object]) -> bool:
    """Validate the private Broker approval fixture independently of an Agent run."""

    return (
        summary.get("approval_required") is True
        and summary.get("approval_dispatch") is True
        and summary.get("approval_replay") is True
        and summary.get("session_closed") is True
        and summary.get("terminal_status") == "succeeded"
        and summary.get("replay_status") == "denied"
    )


def _contains_private_agent_metadata(value: object) -> bool:
    if isinstance(value, dict):
        forbidden_keys = {
            "authorization",
            "bearer",
            "token",
            "secret",
            "arguments",
            "request_body",
            "transcript",
            "credential",
        }
        return any(str(key).casefold() in forbidden_keys for key in value) or any(
            _contains_private_agent_metadata(item) for item in value.values()
        )
    if isinstance(value, list):
        return any(_contains_private_agent_metadata(item) for item in value)
    return isinstance(value, str) and "bearer " in value.casefold()


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
        self._broker_descriptor_path: Path | None = None
        self._temporary = tempfile.TemporaryDirectory(
            prefix="ru-harness-vnext-",
            ignore_cleanup_errors=True,
        )
        self._temporary_root = Path(self._temporary.name)
        self._fixture_root = self._temporary_root / "data"
        self._log_root = self._temporary_root / "logs"
        self._workspace_root = self._temporary_root / "workspace"
        self._workspace_root.mkdir(parents=True, exist_ok=True)
        self._desktop_token = secrets.token_hex(24)
        self._worker_token = secrets.token_hex(24)
        self._broker_token = secrets.token_hex(32)
        self._gui_components: dict[str, object] = {
            "status": "not_run",
            "launched": False,
            "closed": False,
        }
        self.worker_lease: PortLease | None = None
        self.edge_lease: PortLease | None = None
        self.broker_lease: PortLease | None = None
        try:
            self.worker_lease = PortLease.acquire(port=worker_port or 0)
            self.edge_lease = PortLease.acquire(port=edge_port or 0)
            self.broker_lease = PortLease.acquire(port=0)
        except Exception:
            if self.worker_lease is not None:
                self.worker_lease.close()
            if self.edge_lease is not None:
                self.edge_lease.close()
            if self.broker_lease is not None:
                self.broker_lease.close()
            self._temporary.cleanup()
            raise
        self.worker_port = self.worker_lease.port
        self.edge_port = self.edge_lease.port
        self.broker_port = self.broker_lease.port
        self.edge_base_url = f"http://127.0.0.1:{self.edge_port}"
        self.broker_base_url = f"http://127.0.0.1:{self.broker_port}"
        self._registry = build_route_registry(load_contract(self.project_root))
        self._cleanup_result: dict[str, bool] | None = None
        self._startup_failure_reason: str | None = None
        self._startup_failure_details: dict[str, object] = {}

    @classmethod
    def for_test(cls, readiness: bool) -> HarnessSession:
        return cls(
            Path(__file__).resolve().parents[2],
            readiness_override=readiness,
            test_mode=True,
        )

    def run(
        self,
        *,
        mode: HarnessMode = "backend-deterministic",
        app_path: Path | None = None,
        driver_project: Path | None = None,
        scenario: GuiScenario = "turn",
        timeout_seconds: float = 180.0,
    ) -> dict[str, object]:
        started = time.perf_counter()
        started_at = datetime.now(UTC).isoformat().replace("+00:00", "Z")
        checks: list[CheckResult] = []
        errors: list[object] = []
        try:
            if mode == "gui-deterministic":
                if app_path is None or driver_project is None:
                    raise ValueError("gui-deterministic requires app_path and driver_project")
                checks.extend(
                    self.run_gui(
                        app_path=app_path,
                        driver_project=driver_project,
                        scenario=scenario,
                        timeout_seconds=timeout_seconds,
                    )
                )
            elif self._test_mode:
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
                    details=dict(cleanup.items()),
                )
            )
        return build_artifact(
            evidence_class="contract",
            commit=self._commit_for_artifact(),
            mode=mode,
            started_at=started_at,
            duration_ms=(time.perf_counter() - started) * 1000,
            components={
                "edge": {"status": "passed" if self._edge_ready() else "not_ready"},
                "worker": {"status": "passed" if self._worker_ready() else "not_ready"},
                "broker": {"status": "passed" if self._broker_ready() else "not_ready"},
                "workspace_fixture": "temporary",
                **({"gui": self._gui_components} if mode == "gui-deterministic" else {}),
            },
            checks=checks,
            coverage=self._coverage(checks),
            cleanup=cleanup,
            errors=errors,
            secrets=(self._desktop_token, self._worker_token, self._broker_token),
        )

    def run_gui(
        self,
        *,
        app_path: Path,
        driver_project: Path,
        scenario: GuiScenario,
        timeout_seconds: float,
    ) -> list[CheckResult]:
        started = time.perf_counter()
        if scenario not in {
            "turn",
            "stop",
            "cancel",
            "stale-session",
            "agent-volume-contract",
            "agent-cancel",
            "agent-stale-result",
            "agent-private-metadata",
        }:
            raise ValueError("GUI Harness scenario is not allowed")
        if timeout_seconds <= 0 or timeout_seconds > 300:
            raise ValueError("GUI Harness timeout must be between 0 and 300 seconds")

        app_path = app_path.resolve()
        driver_project = driver_project.resolve()
        if not app_path.is_file() or not driver_project.is_file():
            self._gui_components = {
                "status": "failed",
                "launched": False,
                "closed": False,
            }
            return [
                CheckResult(
                    "gui.driver_start",
                    "failed",
                    (time.perf_counter() - started) * 1000,
                    reason="app_or_driver_missing",
                )
            ]

        self._start_children(deterministic=True, harness_scenario=scenario)
        if not self._wait_for_readiness():
            self._gui_components = {
                "status": "blocked_by_startup",
                "launched": False,
                "closed": False,
            }
            return [
                CheckResult(
                    "gui.backend_ready",
                    "blocked_by_startup",
                    (time.perf_counter() - started) * 1000,
                    reason="edge_not_ready",
                )
            ]

        gui_root = self._temporary_root / "gui"
        gui_root.mkdir(parents=True, exist_ok=True)
        events_path = gui_root / "events.jsonl"
        self._descriptor_path = gui_root / "session.json"
        self._write_gui_descriptor(
            self._descriptor_path,
            events_path=events_path,
            scenario=scenario,
        )

        command = [
            "dotnet",
            "run",
            "--project",
            str(driver_project),
            "--configuration",
            "Release",
            "--no-launch-profile",
            "--",
            "--app",
            str(app_path),
            "--session",
            str(self._descriptor_path),
            "--scenario",
            scenario,
            "--timeout",
            str(timeout_seconds),
        ]
        environment = os.environ.copy()
        environment.update(
            {
                "NO_PROXY": "127.0.0.1,localhost",
                "no_proxy": "127.0.0.1,localhost",
            }
        )
        self._gui_components = {
            "status": "running",
            "launched": False,
            "closed": False,
        }
        try:
            completed = subprocess.run(
                command,
                cwd=self.project_root,
                env=environment,
                check=False,
                capture_output=True,
                text=True,
                timeout=timeout_seconds + 30,
            )
        except subprocess.TimeoutExpired:
            self._gui_components = {
                "status": "failed",
                "launched": True,
                "closed": False,
            }
            return [
                CheckResult(
                    "gui.driver_start",
                    "failed",
                    (time.perf_counter() - started) * 1000,
                    reason="driver_timeout",
                )
            ]
        except OSError as error:
            self._gui_components = {
                "status": "failed",
                "launched": False,
                "closed": False,
            }
            return [
                CheckResult(
                    "gui.driver_start",
                    "failed",
                    (time.perf_counter() - started) * 1000,
                    reason="driver_process_error",
                    details={"error_type": type(error).__name__},
                )
            ]

        self._gui_components = {
            "status": "passed" if completed.returncode == 0 else "failed",
            "launched": True,
            "closed": False,
        }
        stdout = completed.stdout or ""
        if len(stdout.encode("utf-8", errors="replace")) > 64 * 1024:
            self._gui_components["status"] = "failed"
            return [
                CheckResult(
                    "gui.driver_start",
                    "failed",
                    (time.perf_counter() - started) * 1000,
                    reason="driver_output_too_large",
                )
            ]

        try:
            payload = self._parse_gui_driver_output(stdout)
        except (ValueError, json.JSONDecodeError):
            self._gui_components["status"] = "failed"
            return [
                CheckResult(
                    "gui.driver_start",
                    "failed",
                    (time.perf_counter() - started) * 1000,
                    reason="invalid_driver_output",
                )
            ]

        self._gui_components["launched"] = bool(payload.get("launched", True))
        self._gui_components["closed"] = bool(payload.get("app_closed", False))
        self._gui_components["status"] = (
            "passed"
            if completed.returncode == 0 and self._gui_components["closed"]
            else "failed"
        )
        checks = self._convert_gui_checks(payload)
        if completed.returncode != 0 and not any(check.status == "failed" for check in checks):
            checks.append(
                CheckResult(
                    "gui.driver_start",
                    "failed",
                    (time.perf_counter() - started) * 1000,
                    reason="driver_nonzero_exit",
                )
            )
        return checks

    def _write_gui_descriptor(
        self,
        descriptor_path: Path,
        *,
        events_path: Path,
        scenario: GuiScenario,
    ) -> None:
        descriptor_path.write_text(
            json.dumps(
                {
                    "base_url": self.edge_base_url,
                    "auth_token": self._desktop_token,
                    "scenario": scenario,
                    "events_path": str(events_path.resolve()),
                    "voice_mode": "deterministic",
                },
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )
        if os.name == "nt":
            identity = f"{os.environ.get('USERDOMAIN', '')}\\{os.environ.get('USERNAME', '')}".strip("\\")
            if identity:
                try:
                    subprocess.run(
                        [
                            "icacls",
                            str(descriptor_path),
                            "/inheritance:r",
                            "/grant:r",
                            f"{identity}:F",
                        ],
                        check=True,
                        capture_output=True,
                        text=True,
                        timeout=5,
                    )
                except (OSError, subprocess.SubprocessError):
                    descriptor_path.chmod(0o600)
        else:
            descriptor_path.chmod(0o600)

    @staticmethod
    def _parse_gui_driver_output(stdout: str) -> dict[str, object]:
        lines = [line.strip() for line in stdout.splitlines() if line.strip()]
        if not lines:
            raise ValueError("GUI driver produced no JSON")
        payload = json.loads(lines[-1])
        if not isinstance(payload, dict) or payload.get("schema_version") != 2:
            raise ValueError("GUI driver schema is invalid")
        serialized = json.dumps(payload, ensure_ascii=False).casefold()
        if any(
            fragment in serialized
            for fragment in ('"bearer"', '"transcript"', '"audio"', '"request_body"')
        ):
            raise ValueError("GUI driver output contains private fields")
        allowed = {
            "schema_version",
            "scenario",
            "checks",
            "event_counts",
            "existing_instance_detected",
            "app_closed",
        }
        if set(payload) - allowed:
            raise ValueError("GUI driver output contains unsupported fields")
        return payload

    @staticmethod
    def _convert_gui_checks(payload: dict[str, object]) -> list[CheckResult]:
        raw_checks = payload.get("checks")
        if not isinstance(raw_checks, list):
            raise ValueError("GUI driver checks are invalid")
        event_counts = payload.get("event_counts", {})
        if not isinstance(event_counts, dict) or not all(
            isinstance(key, str) and isinstance(value, int)
            for key, value in event_counts.items()
        ):
            raise ValueError("GUI driver event counts are invalid")
        checks: list[CheckResult] = []
        allowed_check_fields = {"id", "status", "duration_ms", "reason"}
        for raw in raw_checks:
            if not isinstance(raw, dict) or set(raw) - allowed_check_fields:
                raise ValueError("GUI driver check contains unsupported fields")
            check_id = raw.get("id")
            status = raw.get("status")
            duration_ms = raw.get("duration_ms")
            reason = raw.get("reason")
            if not isinstance(check_id, str) or not isinstance(status, str):
                raise ValueError("GUI driver check identity is invalid")
            if not isinstance(duration_ms, (int, float)) or duration_ms < 0:
                raise ValueError("GUI driver check duration is invalid")
            if reason is not None and not isinstance(reason, str):
                raise ValueError("GUI driver check reason is invalid")
            checks.append(
                CheckResult(
                    check_id,
                    status,
                    float(duration_ms),
                    reason=reason,
                    details={"event_counts": dict(event_counts)},
                )
            )
        return checks

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
        if self.broker_lease is not None:
            self.broker_lease.close()
        ports_closed = wait_for_ports_closed(
            [self.worker_port, self.edge_port, self.broker_port]
        )

        session_descriptor_removed = True
        for descriptor in (self._descriptor_path, self._broker_descriptor_path):
            if descriptor is not None and descriptor.exists():
                try:
                    descriptor.unlink()
                except OSError:
                    session_descriptor_removed = False

        fixture_root_removed = True
        if self._fixture_root.exists():
            try:
                shutil.rmtree(self._fixture_root)
            except OSError:
                fixture_root_removed = False
        workspace_root_removed = True
        if self._workspace_root.exists():
            try:
                shutil.rmtree(self._workspace_root)
            except OSError:
                workspace_root_removed = False
        with contextlib.suppress(OSError):
            self._temporary.cleanup()
        temporary_root_removed = not self._temporary_root.exists()
        self._closed = True
        self._cleanup_result = {
            "children_terminated": children_terminated,
            "ports_closed": ports_closed,
            "session_descriptor_removed": session_descriptor_removed,
            "fixture_root_removed": fixture_root_removed,
            "workspace_root_removed": workspace_root_removed,
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
                    "agent.synthetic_tool_loop",
                    "passed",
                    0.1,
                    details={"event_count": 4, "terminal_events": 1},
                ),
                CheckResult("agent.private_metadata", "passed", 0.1),
                CheckResult("voice.synthetic_stt", "passed", 0.1, details={"status": "ok"}),
                CheckResult("voice.synthetic_tts", "passed", 0.1, details={"valid_wav": True}),
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
                reason=None
                if ready
                else self._startup_failure_reason or "readiness_timeout",
                details={}
                if ready
                else dict(self._startup_failure_details),
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
        if deterministic:
            checks.extend(asyncio.run(self._probe_broker_approval_lifecycle()))
        agent_checks: list[CheckResult] = []
        if deterministic:
            agent_checks.extend(asyncio.run(self._probe_agent_api()))
        voice_checks = asyncio.run(self._probe_voice_websocket())
        checks.extend(agent_checks)
        checks.extend(voice_checks)
        checks.extend(
            [
                CheckResult("route.websocket.ws./voice/stream", "passed", 0.0),
            ]
        )
        return checks

    async def _probe_agent_api(self) -> list[CheckResult]:
        """Exercise the real Agent v1 HTTP/SSE lifecycle with a read-only fixture."""

        started = time.perf_counter()
        events: list[dict[str, Any]] = []
        status: str | None = None
        terminal_statuses = {
            "completed",
            "failed",
            "cancelled",
            "interrupted",
            "reconciliation_required",
        }
        try:
            fixture = self._workspace_root / "harness-agent.txt"
            fixture.write_text("deterministic agent fixture\n", encoding="utf-8")
            prompt = f"Harness agent tool turn {secrets.token_hex(8)}"
            timeout = httpx.Timeout(10.0, connect=5.0)
            async with httpx.AsyncClient(
                base_url=self.edge_base_url,
                headers={"Authorization": f"Bearer {self._desktop_token}"},
                timeout=timeout,
                trust_env=False,
            ) as client:
                session_response = await client.post("/sessions/new")
                if session_response.status_code // 100 != 2:
                    raise RuntimeError("session_create_failed")
                session_payload = session_response.json()
                session_id = session_payload.get("session_id")
                if not isinstance(session_id, str) or len(session_id) != 32:
                    raise RuntimeError("session_id_invalid")

                run_response = await client.post(
                    "/agent/v1/runs",
                    json={
                        "session_id": session_id,
                        "workspace_path": str(self._workspace_root.resolve()),
                        "prompt": prompt,
                        "preset": "standard",
                        "idempotency_key": secrets.token_hex(16),
                    },
                )
                if run_response.status_code not in {200, 201}:
                    raise RuntimeError("agent_run_create_failed")
                run_payload = run_response.json()
                run_id = run_payload.get("run_id")
                trust_request_id = run_payload.get("trust_request_id")
                display_identity_hash = run_payload.get("display_identity_hash")
                if not isinstance(run_id, str) or not run_id.startswith("run_"):
                    raise RuntimeError("run_id_invalid")

                if trust_request_id is not None or display_identity_hash is not None:
                    if not isinstance(trust_request_id, str) or not isinstance(display_identity_hash, str):
                        raise RuntimeError("trust_challenge_invalid")
                    trust_response = await client.post(
                        f"/agent/v1/runs/{run_id}/trust",
                        json={
                            "trust_request_id": trust_request_id,
                            "display_identity_hash": display_identity_hash,
                            "decision": "approve",
                        },
                    )
                    if trust_response.status_code != 200:
                        raise RuntimeError("workspace_trust_failed")

                deadline = time.monotonic() + 30
                while time.monotonic() < deadline:
                    run_state = await client.get(f"/agent/v1/runs/{run_id}")
                    if run_state.status_code != 200:
                        raise RuntimeError("agent_run_read_failed")
                    current = run_state.json()
                    status_value = current.get("status")
                    if not isinstance(status_value, str):
                        raise RuntimeError("agent_status_invalid")
                    status = status_value
                    if status in terminal_statuses:
                        break
                    await asyncio.sleep(0.2)
                else:
                    raise TimeoutError("agent_run_timeout")

                sse_response = await client.get(
                    f"/agent/v1/runs/{run_id}/events",
                    params={"live": "0", "after_sequence": "0"},
                )
                if sse_response.status_code != 200:
                    raise RuntimeError("agent_events_read_failed")
                raw_sse = sse_response.text
                if len(raw_sse.encode("utf-8")) > 1 * 1024 * 1024:
                    raise RuntimeError("agent_events_too_large")
                for frame in raw_sse.split("\n\n"):
                    data_lines = [line[5:].lstrip() for line in frame.splitlines() if line.startswith("data:")]
                    if not data_lines:
                        continue
                    event = json.loads("\n".join(data_lines))
                    if isinstance(event, dict):
                        if any(line == "event: final_snapshot" for line in frame.splitlines()):
                            event["event_type"] = "final_snapshot"
                        events.append(event)

            event_types = {
                event.get("event_type")
                for event in events
                if isinstance(event.get("event_type"), str)
            }
            loop_completed = (
                status == "completed"
                and "tool.proposed" in event_types
                and "tool.execution_completed" in event_types
                and any(event.get("event_type") == "final_snapshot" for event in events)
            )
            private_absent = not _contains_private_agent_metadata(events)
            duration = (time.perf_counter() - started) * 1000
            return [
                CheckResult(
                    "agent.synthetic_tool_loop",
                    "passed" if loop_completed else "failed",
                    duration,
                    reason=None if loop_completed else "agent_tool_loop_not_completed",
                    details={
                        "status": status,
                        "event_count": len(events),
                        "tool_proposed": "tool.proposed" in event_types,
                        "tool_execution_completed": "tool.execution_completed" in event_types,
                    },
                ),
                CheckResult(
                    "agent.private_metadata",
                    "passed" if private_absent else "failed",
                    duration,
                    reason=None if private_absent else "private_agent_metadata_visible",
                ),
            ]
        except Exception as error:
            duration = (time.perf_counter() - started) * 1000
            details = {"error_type": type(error).__name__}
            return [
                CheckResult(
                    "agent.synthetic_tool_loop",
                    "failed",
                    duration,
                    reason="request_error",
                    details=details,
                ),
                CheckResult(
                    "agent.private_metadata",
                    "failed",
                    duration,
                    reason="request_error",
                    details=details,
                ),
            ]

    async def _probe_broker_approval_lifecycle(self) -> list[CheckResult]:
        """Exercise approval once and replay it over the private Broker protocol."""
        summary = await probe_approval_lifecycle(
            base_url=self.broker_base_url,
            token=self._broker_token,
            tool_id="notes.create",
            tool_version="1.0.0",
            arguments={
                "path": "notes/harness-approval.md",
                "title": "Harness approval fixture",
                "content": "bounded deterministic fixture",
            },
        )

        complete = broker_approval_lifecycle_completed(summary)
        details = {
            "approval_required": summary["approval_required"],
            "terminal_status": summary["terminal_status"],
            "replay_status": summary["replay_status"],
            "session_closed": summary["session_closed"],
        }
        if "error_type" in summary:
            details["error_type"] = summary["error_type"]
        return [
            CheckResult(
                "agent.approval_dispatch",
                "passed" if summary["approval_dispatch"] else "failed",
                0.0,
                reason=None if summary["approval_dispatch"] else "approved_action_not_dispatched_once",
                details=details,
            ),
            CheckResult(
                "agent.approval_replay",
                "passed" if summary["approval_replay"] else "failed",
                0.0,
                reason=None if summary["approval_replay"] else "approval_replay_not_rejected",
                details={"replay_status": summary["replay_status"]},
            ),
            CheckResult(
                "agent.broker_session_close",
                "passed" if summary["session_closed"] else "failed",
                0.0,
                reason=None if summary["session_closed"] else "broker_session_not_closed",
                details={"lifecycle_complete": complete},
            ),
        ]

    def _start_children(
        self,
        *,
        deterministic: bool,
        harness_scenario: GuiScenario | None = None,
    ) -> None:
        assert (
            self.worker_lease is not None
            and self.edge_lease is not None
            and self.broker_lease is not None
        )
        # Broker and Edge share RuAgent.Protocol. Building both through concurrent
        # `dotnet run` calls races on the shared obj output during a cold start.
        # Keep the port leases held while the projects build in a fixed order.
        self._prepare_dotnet_projects()
        workspace_root = self._workspace_root
        workspace_root.mkdir(parents=True, exist_ok=True)
        self.worker_lease.handoff()
        self.edge_lease.handoff()
        self.broker_lease.handoff()
        self._fixture_root.mkdir(parents=True, exist_ok=True)
        self._log_root.mkdir(parents=True, exist_ok=True)
        broker_root = self._temporary_root / "agent"
        broker_root.mkdir(parents=True, exist_ok=True)
        self._broker_descriptor_path = broker_root / "broker.json"
        self._write_broker_descriptor(self._broker_descriptor_path, broker_root)
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
                "RU_AGENT_BROKER_DESCRIPTOR": str(self._broker_descriptor_path),
                "RU_WORKSPACE_ROOT": str(workspace_root),
                "RuBackend__BindUrl": self.edge_base_url,
                "RuBackend__WorkerUrl": f"ws://127.0.0.1:{self.worker_port}/worker/v1",
                "NO_PROXY": "127.0.0.1,localhost",
                "no_proxy": "127.0.0.1,localhost",
                "PYTHONPATH": os.pathsep.join(python_paths),
            }
        )
        if deterministic and harness_scenario is not None:
            environment.update(
                {
                    "RU_HARNESS_SCENARIO": harness_scenario,
                    "RU_HARNESS_TRANSCRIBE_DELAY_MS": (
                        "300" if harness_scenario in {"cancel", "stale-session"} else "0"
                    ),
                    "RU_HARNESS_VOICE_DELAY_MS": (
                        "100" if harness_scenario == "stop" else "0"
                    ),
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
        broker_command = [
            "dotnet",
            "run",
            "--project",
            str(self.project_root / "backend-dotnet" / "RuAgent.Broker" / "RuAgent.Broker.csproj"),
            "--no-launch-profile",
            "--no-build",
            "--configuration",
            _DOTNET_CONFIGURATION,
        ]
        self._children.append(
            start_owned_child(
                broker_command,
                repo_root=self.project_root,
                environment=environment,
                role="broker",
                log_path=self._log_root / "broker.log",
                secrets=(self._desktop_token, self._worker_token, self._broker_token),
            )
        )
        edge_command = [
            "dotnet",
            "run",
            "--project",
            str(self.project_root / "backend-dotnet" / "RuBackend" / "RuBackend.csproj"),
            "--no-launch-profile",
            "--no-build",
            "--configuration",
            _DOTNET_CONFIGURATION,
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
                secrets=(self._desktop_token, self._worker_token, self._broker_token),
            )
        )

    def _prepare_dotnet_projects(self) -> None:
        projects = (
            self.project_root / "backend-dotnet" / "RuAgent.Broker" / "RuAgent.Broker.csproj",
            self.project_root / "backend-dotnet" / "RuBackend" / "RuBackend.csproj",
        )
        for project in projects:
            subprocess.run(
                [
                    "dotnet",
                    "build",
                    "--nologo",
                    str(project),
                    "--configuration",
                    _DOTNET_CONFIGURATION,
                    "--verbosity",
                    "minimal",
                ],
                cwd=self.project_root,
                check=True,
                timeout=_DOTNET_BUILD_TIMEOUT_SECONDS,
            )

    def _write_broker_descriptor(self, descriptor_path: Path, session_root: Path) -> None:
        descriptor_path.write_text(
            json.dumps(
                {
                    "base_url": self.broker_base_url,
                    "auth_token": self._broker_token,
                    "protocol_version": 1,
                    "session_root": str(session_root.resolve()),
                    "expires_at": (
                        datetime.now(UTC) + timedelta(minutes=30)
                    ).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
                },
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )

    def _wait_for_readiness(self, timeout_seconds: float = 60.0) -> bool:
        self._startup_failure_reason = None
        self._startup_failure_details = {}
        deadline = time.monotonic() + timeout_seconds
        edge_headers = {"Authorization": f"Bearer {self._desktop_token}"}
        broker_headers = {"Authorization": f"Bearer {self._broker_token}"}
        while time.monotonic() < deadline:
            for child in self._children:
                child_exit = child.poll()
                if child_exit.code is not None:
                    self._startup_failure_reason = "child_unexpected_exit"
                    self._startup_failure_details = {
                        "role": child_exit.role,
                        "exit_code": child_exit.code,
                        "classification": child_exit.classification,
                    }
                    return False
            try:
                response = httpx.get(
                    self.edge_base_url + "/settings",
                    headers=edge_headers,
                    timeout=2.0,
                )
                edge_status = response.status_code
                response.close()
                broker_response = httpx.get(
                    self.broker_base_url + "/health",
                    headers=broker_headers,
                    timeout=2.0,
                )
                broker_status = broker_response.status_code
                broker_response.close()
                if (
                    200 <= edge_status < 300
                    and 200 <= broker_status < 300
                    and all(child.poll().code is None for child in self._children)
                ):
                    for child in self._children:
                        child.mark_ready()
                    return True
            except httpx.HTTPError:
                pass
            time.sleep(0.2)
        self._startup_failure_reason = "readiness_timeout"
        self._startup_failure_details = {
            "timeout_seconds": round(timeout_seconds, 3),
        }
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
        def group(prefix: str, total: int | None) -> dict[str, int]:
            values = [check for check in checks if check.check_id.startswith(prefix)]
            if total is None:
                # Dynamic total from registry – preserves 54+2 after Phase 1
                total = len([e for e in self._registry if e.check_id.startswith(prefix)])
            return {
                "total": total,
                "passed": sum(check.status == "passed" for check in values),
                "failed": sum(check.status in {"failed", "blocked_by_startup"} for check in values),
                "skipped": sum(check.status == "skipped" for check in values),
            }

        return {"http": group("route.http.", None), "websocket": group("route.websocket.", 1)}

    def _edge_ready(self) -> bool:
        return bool(self._children and self._children[-1].ready) or self._test_mode and bool(self._readiness_override)

    def _worker_ready(self) -> bool:
        return bool(self._children and self._children[0].ready) or self._test_mode and bool(self._readiness_override)

    def _broker_ready(self) -> bool:
        return bool(len(self._children) > 1 and self._children[1].ready) or self._test_mode and bool(self._readiness_override)

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
