"""Explicit, opt-in native acceptance for the published Ru Agent Broker.

This command changes Windows volume by one point and restores the original
state. It is intentionally excluded from CI and deterministic Harness runs.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import re
import secrets
import socket
import subprocess
import tempfile
import time
from collections.abc import Callable, Mapping
from dataclasses import dataclass, replace
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Protocol

import httpx
import psutil
from websockets.asyncio.client import connect

from harness.artifacts import build_artifact
from harness.model import CheckResult

_ROLE = "default_render_multimedia"
_HASH_PATTERN = re.compile(r"^[0-9a-f]{16}$")
_ERROR_PATTERN = re.compile(r"^[a-z][a-z0-9_]{0,63}$")
_STATUSES = frozenset(
    {"succeeded", "failed", "denied", "approval_required", "cancelled", "timed_out"}
)
_VERIFICATION_STATES = frozenset({"not_required", "pending", "verified", "failed"})


class NativeAcceptanceError(Exception):
    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


@dataclass(frozen=True, slots=True)
class NativeRunConfig:
    broker_exe: Path
    output: Path
    timeout_seconds: float = 20.0

    def __post_init__(self) -> None:
        broker_input = Path(self.broker_exe)
        output_input = Path(self.output)
        if not broker_input.is_absolute() or not output_input.is_absolute():
            raise ValueError("paths_must_be_absolute")
        broker = broker_input.resolve()
        output = output_input.resolve()
        if broker.name.casefold() != "ruagent.broker.exe":
            raise ValueError("invalid_broker_executable")
        if "publish" not in {part.casefold() for part in broker.parts} or not broker.is_file():
            raise ValueError("broker_must_be_published")
        if output.suffix.casefold() != ".json" or output == broker or output.is_dir():
            raise ValueError("invalid_output_path")
        if not 5 <= self.timeout_seconds <= 60:
            raise ValueError("invalid_timeout")
        object.__setattr__(self, "broker_exe", broker)
        object.__setattr__(self, "output", output)


@dataclass(frozen=True, slots=True)
class VolumeReceipt:
    status: str
    error_code: str | None
    duration_ms: int
    verification_state: str
    endpoint_role: str | None
    endpoint_hash: str | None
    level: int | None
    muted: bool | None

    @classmethod
    def from_payload(cls, payload: object) -> "VolumeReceipt":
        if not isinstance(payload, Mapping):
            raise NativeAcceptanceError("malformed_receipt")
        allowed = {
            "status",
            "output",
            "error_code",
            "safe_summary",
            "duration_ms",
            "verification_state",
            "endpoint_role",
            "endpoint_hash",
            "adapter_invoked",
        }
        if set(payload) - allowed:
            raise NativeAcceptanceError("malformed_receipt")
        status = payload.get("status")
        duration = payload.get("duration_ms")
        verification = payload.get("verification_state")
        error_code = payload.get("error_code")
        role = payload.get("endpoint_role")
        endpoint_hash = payload.get("endpoint_hash")
        adapter_invoked = payload.get("adapter_invoked")
        if (
            status not in _STATUSES
            or isinstance(duration, bool)
            or not isinstance(duration, int)
            or duration not in range(0, 300_001)
            or verification not in _VERIFICATION_STATES
            or not isinstance(adapter_invoked, bool)
        ):
            raise NativeAcceptanceError("malformed_receipt")
        if error_code is not None and (
            not isinstance(error_code, str) or _ERROR_PATTERN.fullmatch(error_code) is None
        ):
            raise NativeAcceptanceError("malformed_receipt")
        if role is not None and role != _ROLE:
            raise NativeAcceptanceError("malformed_receipt")
        if endpoint_hash is not None and (
            not isinstance(endpoint_hash, str) or _HASH_PATTERN.fullmatch(endpoint_hash) is None
        ):
            raise NativeAcceptanceError("malformed_receipt")

        level: int | None = None
        muted: bool | None = None
        output = payload.get("output")
        if not isinstance(output, Mapping):
            raise NativeAcceptanceError("malformed_receipt")
        if status == "succeeded":
            if set(output) - {"operation", "level", "muted"}:
                raise NativeAcceptanceError("malformed_receipt")
            level_value = output.get("level")
            muted_value = output.get("muted")
            operation = output.get("operation")
            if (
                isinstance(level_value, bool)
                or not isinstance(level_value, int)
                or level_value not in range(0, 101)
                or not isinstance(muted_value, bool)
                or operation not in {"get", "set", "increase", "decrease", "mute", "unmute"}
                or verification != "verified"
                or role != _ROLE
                or not isinstance(endpoint_hash, str)
                or not adapter_invoked
            ):
                raise NativeAcceptanceError("malformed_receipt")
            level = level_value
            muted = muted_value
        elif status == "approval_required":
            if set(output) - {
                "approval_id",
                "tool_id",
                "tool_version",
                "scope",
                "safe_action",
                "expires_at",
            }:
                raise NativeAcceptanceError("malformed_receipt")
        elif output:
            raise NativeAcceptanceError("malformed_receipt")
        return cls(
            status=status,
            error_code=error_code,
            duration_ms=duration,
            verification_state=verification,
            endpoint_role=role,
            endpoint_hash=endpoint_hash,
            level=level,
            muted=muted,
        )


@dataclass(frozen=True, slots=True)
class NativeAcceptanceResult:
    passed: bool
    restored: bool
    endpoint_role: str | None
    endpoint_hash: str | None
    mutation_status: str
    verification_status: str
    restoration_status: str
    mutation_duration_ms: int
    restoration_duration_ms: int
    error_codes: tuple[str, ...]


class VolumeProtocol(Protocol):
    async def open(self) -> None: ...
    async def get_volume(self, label: str) -> VolumeReceipt: ...
    async def change_volume(self, label: str, operation: str) -> VolumeReceipt: ...
    async def restore_volume(self, label: str, original: VolumeReceipt) -> VolumeReceipt: ...
    async def close(self) -> None: ...


class NativeAcceptanceRunner:
    def __init__(self, client: VolumeProtocol) -> None:
        self._client = client

    def run_sync(self) -> NativeAcceptanceResult:
        return asyncio.run(self.run())

    async def run(self) -> NativeAcceptanceResult:
        original: VolumeReceipt | None = None
        endpoint_role: str | None = None
        endpoint_hash: str | None = None
        mutation_status = "not_run"
        verification_status = "not_run"
        restoration_status = "not_run"
        mutation_duration = 0
        restoration_duration = 0
        restored = False
        mutation_verified = False
        error_codes: list[str] = []
        try:
            await self._client.open()
            original = self._require_verified(await self._client.get_volume("get-original"))
            endpoint_role = original.endpoint_role
            endpoint_hash = original.endpoint_hash
            operation = "increase" if original.level != 100 else "decrease"
            expected_level = original.level + (1 if operation == "increase" else -1)
            changed = self._require_verified(
                await self._client.change_volume("change-one-point", operation),
                role=endpoint_role,
                endpoint_hash=endpoint_hash,
            )
            mutation_status = changed.status
            mutation_duration += changed.duration_ms
            verified = self._require_verified(
                await self._client.get_volume("verify-change"),
                role=endpoint_role,
                endpoint_hash=endpoint_hash,
            )
            verification_status = verified.status
            mutation_duration += verified.duration_ms
            if (
                changed.level != expected_level
                or verified.level != expected_level
                or changed.muted != original.muted
                or verified.muted != original.muted
            ):
                raise NativeAcceptanceError("change_not_verified")
            mutation_verified = True
        except BaseException as error:  # restoration also applies to Ctrl+C
            error_codes.append(_stable_error_code(error))
            if mutation_status == "not_run":
                mutation_status = "failed"
            if verification_status == "not_run":
                verification_status = "failed"
        finally:
            if original is not None:
                try:
                    restored_receipt = self._require_verified(
                        await self._client.restore_volume("restore", original),
                        role=endpoint_role,
                        endpoint_hash=endpoint_hash,
                    )
                    restoration_status = restored_receipt.status
                    restoration_duration += restored_receipt.duration_ms
                except BaseException as error:
                    restoration_status = "failed"
                    error_codes.append(_stable_error_code(error, fallback="restore_failed"))
                try:
                    restore_verification = self._require_verified(
                        await self._client.get_volume("verify-restore"),
                        role=endpoint_role,
                        endpoint_hash=endpoint_hash,
                    )
                    restoration_duration += restore_verification.duration_ms
                    restored = (
                        restoration_status == "succeeded"
                        and restore_verification.level == original.level
                        and restore_verification.muted == original.muted
                    )
                    if not restored:
                        error_codes.append("restore_not_verified")
                except BaseException as error:
                    restored = False
                    error_codes.append(_stable_error_code(error, fallback="restore_not_verified"))
            try:
                await self._client.close()
            except BaseException:
                error_codes.append("session_close_failed")

        deduplicated = tuple(dict.fromkeys(error_codes))
        return NativeAcceptanceResult(
            passed=mutation_verified and restored and not deduplicated,
            restored=restored,
            endpoint_role=endpoint_role,
            endpoint_hash=endpoint_hash,
            mutation_status=mutation_status,
            verification_status=verification_status,
            restoration_status=restoration_status,
            mutation_duration_ms=mutation_duration,
            restoration_duration_ms=restoration_duration,
            error_codes=deduplicated,
        )

    @staticmethod
    def _require_verified(
        receipt: object,
        *,
        role: str | None = None,
        endpoint_hash: str | None = None,
    ) -> VolumeReceipt:
        if not isinstance(receipt, VolumeReceipt):
            raise NativeAcceptanceError("malformed_receipt")
        if (
            receipt.status != "succeeded"
            or receipt.verification_state != "verified"
            or receipt.endpoint_role != _ROLE
            or receipt.endpoint_hash is None
            or _HASH_PATTERN.fullmatch(receipt.endpoint_hash) is None
            or receipt.level is None
            or receipt.muted is None
        ):
            raise NativeAcceptanceError(receipt.error_code or "unverified_receipt")
        if role is not None and receipt.endpoint_role != role:
            raise NativeAcceptanceError("endpoint_changed")
        if endpoint_hash is not None and receipt.endpoint_hash != endpoint_hash:
            raise NativeAcceptanceError("endpoint_changed")
        return receipt


class BrokerProtocolClient:
    def __init__(
        self,
        base_url: str,
        token: str,
        timeout_seconds: float,
        *,
        websocket_connect: Callable[..., Any] = connect,
    ) -> None:
        self._uri = base_url.replace("http://", "ws://", 1) + "/agent"
        self._token = token
        self._timeout = timeout_seconds
        self._connect = websocket_connect
        self._socket: Any = None
        self._request_number = 0
        self._session_id = "native-volume-acceptance"
        self._correlation_id = secrets.token_hex(16)

    async def open(self) -> None:
        self._socket = await self._connect(
            self._uri,
            additional_headers={"Authorization": f"Bearer {self._token}"},
            proxy=None,
            open_timeout=self._timeout,
            close_timeout=2,
        )
        hello = await self._exchange("hello", {})
        tools = hello["payload"].get("tools")
        if hello.get("message_type") != "hello_ok" or not isinstance(tools, list):
            raise NativeAcceptanceError("broker_hello_failed")
        volume = [
            tool
            for tool in tools
            if isinstance(tool, Mapping)
            and tool.get("tool_id") == "system.volume"
            and tool.get("tool_version") == "1.0.0"
        ]
        if len(volume) != 1 or volume[0].get("scopes") != ["system.write"]:
            raise NativeAcceptanceError("volume_capability_unavailable")
        opened = await self._exchange(
            "session_open",
            {"scopes": ["system.write"], "skill_ids": []},
        )
        if opened.get("message_type") != "session_opened" or opened["payload"].get("status") != "succeeded":
            raise NativeAcceptanceError("session_open_failed")

    async def get_volume(self, label: str) -> VolumeReceipt:
        del label
        return await self._execute({"operation": "get"})

    async def change_volume(self, label: str, operation: str) -> VolumeReceipt:
        del label
        if operation not in {"increase", "decrease"}:
            raise NativeAcceptanceError("invalid_change_operation")
        return await self._execute({"operation": operation, "amount": 1})

    async def restore_volume(self, label: str, original: VolumeReceipt) -> VolumeReceipt:
        del label
        if original.level is None or original.muted is None:
            raise NativeAcceptanceError("missing_original_state")
        restored = await self._execute({"operation": "set", "level": original.level})
        if restored.status != "succeeded" or restored.muted == original.muted:
            return restored
        operation = "mute" if original.muted else "unmute"
        return await self._execute({"operation": operation})

    async def close(self) -> None:
        socket = self._socket
        self._socket = None
        if socket is None:
            return
        try:
            await self._exchange_on(socket, "session_close", {})
        finally:
            await socket.close()

    async def _execute(self, arguments: dict[str, object]) -> VolumeReceipt:
        reply = await self._exchange(
            "tool_request",
            {
                "tool_id": "system.volume",
                "tool_version": "1.0.0",
                "capability_class": "native_mutation",
                "arguments": arguments,
            },
        )
        receipt = VolumeReceipt.from_payload(reply["payload"])
        if receipt.status != "approval_required":
            return receipt
        output = reply["payload"].get("output")
        approval_id = output.get("approval_id") if isinstance(output, Mapping) else None
        if not isinstance(approval_id, str) or not approval_id:
            raise NativeAcceptanceError("malformed_approval")
        approved = await self._exchange(
            "approval_response",
            {"approval_id": approval_id, "approved": True},
        )
        return VolumeReceipt.from_payload(approved["payload"])

    async def _exchange(self, message_type: str, payload: dict[str, object]) -> dict[str, Any]:
        if self._socket is None:
            raise NativeAcceptanceError("broker_not_connected")
        return await self._exchange_on(self._socket, message_type, payload)

    async def _exchange_on(
        self,
        socket: Any,
        message_type: str,
        payload: dict[str, object],
    ) -> dict[str, Any]:
        self._request_number += 1
        request_id = f"native-{self._request_number}"
        await socket.send(
            json.dumps(
                {
                    "protocol_version": 1,
                    "message_type": message_type,
                    "request_id": request_id,
                    "session_id": self._session_id,
                    "correlation_id": self._correlation_id,
                    "deadline_ms": int(self._timeout * 1_000),
                    "payload": payload,
                }
            )
        )
        raw = await asyncio.wait_for(socket.recv(), timeout=self._timeout)
        if not isinstance(raw, str):
            raise NativeAcceptanceError("malformed_broker_reply")
        reply = json.loads(raw)
        if (
            not isinstance(reply, dict)
            or reply.get("protocol_version") != 1
            or reply.get("request_id") != request_id
            or reply.get("session_id") != self._session_id
            or reply.get("correlation_id") != self._correlation_id
            or not isinstance(reply.get("payload"), dict)
        ):
            raise NativeAcceptanceError("broker_context_mismatch")
        return reply


def _allocate_loopback_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return int(listener.getsockname()[1])


def prove_bearer_ownership(
    base_url: str,
    correct_token: str,
    wrong_token: str,
    *,
    timeout_seconds: float,
    http_get: Callable[..., Any] = httpx.get,
) -> bool:
    statuses: list[int] = []
    for token in (correct_token, wrong_token):
        response = http_get(
            base_url + "/health",
            headers={"Authorization": f"Bearer {token}"},
            timeout=timeout_seconds,
        )
        try:
            statuses.append(int(response.status_code))
        finally:
            response.close()
    return statuses == [200, 401]


class PublishedBrokerProcess:
    def __init__(
        self,
        config: NativeRunConfig,
        *,
        port_allocator: Callable[[], int] = _allocate_loopback_port,
        popen: Callable[..., subprocess.Popen[Any]] = subprocess.Popen,
        http_get: Callable[..., Any] = httpx.get,
    ) -> None:
        self._config = config
        self._port_allocator = port_allocator
        self._popen = popen
        self._http_get = http_get
        self._temporary: tempfile.TemporaryDirectory[str] | None = None
        self._temporary_root: Path | None = None
        self._descriptor: Path | None = None
        self._process: subprocess.Popen[Any] | None = None
        self.base_url = ""
        self.token = ""

    def start(self) -> None:
        if self._process is not None:
            raise NativeAcceptanceError("process_already_started")
        port = self._port_allocator()
        self.base_url = f"http://127.0.0.1:{port}"
        self.token = secrets.token_hex(32)
        self._temporary = tempfile.TemporaryDirectory(prefix="ru-native-volume-")
        self._temporary_root = Path(self._temporary.name)
        session_root = self._temporary_root / "session"
        workspace_root = self._temporary_root / "workspace"
        session_root.mkdir(parents=True)
        workspace_root.mkdir(parents=True)
        self._descriptor = session_root / "broker.json"
        self._descriptor.write_text(
            json.dumps(
                {
                    "base_url": self.base_url,
                    "auth_token": self.token,
                    "protocol_version": 1,
                    "session_root": str(session_root.resolve()),
                    "expires_at": (
                        datetime.now(timezone.utc) + timedelta(minutes=5)
                    ).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
                }
            ),
            encoding="utf-8",
        )
        self._descriptor.chmod(0o600)
        environment = self._child_environment(self._descriptor, workspace_root)
        creation_flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        self._process = self._popen(
            [str(self._config.broker_exe)],
            cwd=self._config.broker_exe.parent,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=creation_flags,
        )

    @staticmethod
    def _child_environment(
        descriptor: Path,
        workspace_root: Path,
        *,
        source: Mapping[str, str] | None = None,
    ) -> dict[str, str]:
        inherited = os.environ if source is None else source
        environment = {
            key: value
            for key in ("PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "TEMP", "TMP")
            if isinstance((value := inherited.get(key)), str) and value
        }
        environment["RU_AGENT_BROKER_DESCRIPTOR"] = str(descriptor)
        environment["RU_WORKSPACE_ROOT"] = str(workspace_root)
        return environment

    def wait_ready(self) -> None:
        if self._process is None:
            raise NativeAcceptanceError("process_not_started")
        deadline = time.monotonic() + self._config.timeout_seconds
        while time.monotonic() < deadline:
            if self._process.poll() is not None:
                raise NativeAcceptanceError("broker_exited")
            try:
                if prove_bearer_ownership(
                    self.base_url,
                    self.token,
                    secrets.token_hex(24),
                    timeout_seconds=min(2.0, self._config.timeout_seconds),
                    http_get=self._http_get,
                ):
                    return
            except (httpx.HTTPError, OSError):
                pass
            time.sleep(0.05)
        raise NativeAcceptanceError("broker_readiness_timeout")

    def close(self) -> dict[str, bool]:
        process_terminated = True
        process = self._process
        self._process = None
        if process is not None and process.poll() is None:
            try:
                owner = psutil.Process(process.pid)
                descendants = owner.children(recursive=True)
                for child in reversed(descendants):
                    child.terminate()
                owner.terminate()
                _, alive = psutil.wait_procs([*descendants, owner], timeout=2)
                for item in alive:
                    item.kill()
                psutil.wait_procs(alive, timeout=2)
                process.wait(timeout=2)
            except (psutil.Error, OSError, subprocess.SubprocessError):
                try:
                    process.kill()
                    process.wait(timeout=2)
                except (OSError, subprocess.SubprocessError):
                    process_terminated = False
        if process is not None and process.poll() is None:
            process_terminated = False

        descriptor_removed = self._descriptor is None or not self._descriptor.exists()
        temporary_root = self._temporary_root
        if self._temporary is not None:
            try:
                self._temporary.cleanup()
            except OSError:
                pass
        temporary_root_removed = temporary_root is None or not temporary_root.exists()
        descriptor_removed = descriptor_removed or self._descriptor is None or not self._descriptor.exists()
        return {
            "process_terminated": process_terminated,
            "descriptor_removed": descriptor_removed,
            "temporary_root_removed": temporary_root_removed,
        }


def build_native_artifact(
    result: NativeAcceptanceResult,
    *,
    cleanup: Mapping[str, object],
    commit: str,
) -> dict[str, object]:
    mutation_passed = result.passed and result.mutation_status == "succeeded"
    restoration_passed = result.restored and result.restoration_status == "succeeded"
    checks = [
        CheckResult(
            "native.volume_change",
            "passed" if mutation_passed else "failed",
            result.mutation_duration_ms,
            reason=None if mutation_passed else (result.error_codes[0] if result.error_codes else "native_change_failed"),
            details={
                "status": result.mutation_status,
                "verification_status": result.verification_status,
                "endpoint_role": result.endpoint_role,
                "endpoint_hash": result.endpoint_hash,
            },
        ),
        CheckResult(
            "native.volume_restore",
            "passed" if restoration_passed else "failed",
            result.restoration_duration_ms,
            reason=None if restoration_passed else "restore_not_verified",
            details={
                "status": result.restoration_status,
                "restored": result.restored,
                "endpoint_role": result.endpoint_role,
                "endpoint_hash": result.endpoint_hash,
            },
        ),
    ]
    if not all(value is True for value in cleanup.values()):
        checks.append(
            CheckResult(
                "native.cleanup",
                "failed",
                0,
                reason="cleanup_failure",
                details=dict(cleanup),
            )
        )
    return build_artifact(
        evidence_class="native",
        commit=commit,
        mode="native-volume-acceptance",
        started_at=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        duration_ms=result.mutation_duration_ms + result.restoration_duration_ms,
        components={
            "broker": {"role": result.endpoint_role, "endpoint_hash": result.endpoint_hash},
        },
        checks=checks,
        coverage={"native_volume": {"attempted": True}},
        cleanup=dict(cleanup),
        errors=[{"code": code} for code in result.error_codes],
    )


def _stable_error_code(error: BaseException, *, fallback: str = "native_acceptance_failed") -> str:
    if isinstance(error, NativeAcceptanceError):
        return error.code
    if isinstance(error, (TimeoutError, asyncio.TimeoutError)):
        return "timeout"
    if isinstance(error, KeyboardInterrupt):
        return "interrupted"
    return fallback


def _write_artifact(path: Path, artifact: Mapping[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(artifact, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(path)


def main(
    argv: list[str] | None = None,
    *,
    process_factory: Callable[[NativeRunConfig], Any] = PublishedBrokerProcess,
    protocol_client_factory: Callable[[str, str, float], VolumeProtocol] = BrokerProtocolClient,
    artifact_writer: Callable[[Path, Mapping[str, object]], None] = _write_artifact,
) -> int:
    parser = argparse.ArgumentParser(description="Run opt-in native Ru volume acceptance")
    parser.add_argument("--broker-exe", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--confirm-native-volume-change", action="store_true")
    parser.add_argument("--timeout-seconds", type=float, default=20.0)
    try:
        args = parser.parse_args(argv)
    except SystemExit as error:
        return int(error.code)
    if not args.confirm_native_volume_change:
        return 2
    try:
        config = NativeRunConfig(
            args.broker_exe,
            args.output,
            args.timeout_seconds,
        )
    except (OSError, ValueError):
        return 2

    process: Any = None
    cleanup: dict[str, bool] = {
        "process_terminated": False,
        "descriptor_removed": False,
        "temporary_root_removed": False,
    }
    result = NativeAcceptanceResult(
        passed=False,
        restored=False,
        endpoint_role=None,
        endpoint_hash=None,
        mutation_status="not_run",
        verification_status="not_run",
        restoration_status="not_run",
        mutation_duration_ms=0,
        restoration_duration_ms=0,
        error_codes=("native_acceptance_failed",),
    )
    try:
        process = process_factory(config)
        process.start()
        process.wait_ready()
        client = protocol_client_factory(process.base_url, process.token, config.timeout_seconds)
        result = NativeAcceptanceRunner(client).run_sync()
    except BaseException as error:
        result = replace(result, error_codes=(_stable_error_code(error),))
    finally:
        if process is not None:
            try:
                cleanup = dict(process.close())
            except BaseException:
                cleanup = {
                    "process_terminated": False,
                    "descriptor_removed": False,
                    "temporary_root_removed": False,
                }

    if not all(cleanup.values()) and result.passed:
        result = replace(result, passed=False, error_codes=(*result.error_codes, "cleanup_failure"))
    artifact = build_native_artifact(result, cleanup=cleanup, commit="published-broker")
    try:
        artifact_writer(config.output, artifact)
    except OSError:
        return 1
    return 0 if result.passed and all(cleanup.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
