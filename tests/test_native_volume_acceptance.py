from __future__ import annotations

import json
import asyncio
from pathlib import Path
from typing import Any

import pytest

from native_volume_acceptance import (
    BrokerProtocolClient,
    NativeAcceptanceRunner,
    NativeAcceptanceError,
    NativeRunConfig,
    VolumeReceipt,
    build_native_artifact,
    main,
    prove_bearer_ownership,
)


ROLE = "default_render_multimedia"
HASH = "0123456789abcdef"


def receipt(
    *,
    level: int = 40,
    muted: bool = False,
    status: str = "succeeded",
    verification_state: str = "verified",
    error_code: str | None = None,
) -> VolumeReceipt:
    return VolumeReceipt(
        status=status,
        error_code=error_code,
        duration_ms=3,
        verification_state=verification_state,
        endpoint_role=ROLE,
        endpoint_hash=HASH,
        level=level,
        muted=muted,
    )


class FakeClient:
    def __init__(self, failure: BaseException | str | None = None) -> None:
        self.failure = failure
        self.calls: list[str] = []

    async def open(self) -> None:
        return None

    async def get_volume(self, label: str) -> VolumeReceipt:
        self.calls.append(label)
        if label == "get-original":
            return receipt(level=40, muted=False)
        if label == "verify-change":
            if self.failure == "malformed":
                return object()  # type: ignore[return-value]
            if isinstance(self.failure, BaseException):
                raise self.failure
            if self.failure == "tool":
                return receipt(status="failed", verification_state="failed", error_code="read_failed")
            return receipt(level=41, muted=False)
        if label == "verify-restore":
            return receipt(level=40, muted=False)
        raise AssertionError(label)

    async def change_volume(self, label: str, operation: str) -> VolumeReceipt:
        self.calls.append(label)
        assert operation == "increase"
        return receipt(level=41, muted=False)

    async def restore_volume(self, label: str, original: VolumeReceipt) -> VolumeReceipt:
        self.calls.append(label)
        if self.failure == "restore":
            return receipt(status="failed", verification_state="failed", error_code="write_failed")
        return receipt(level=original.level or 0, muted=bool(original.muted))

    async def close(self) -> None:
        return None


def test_no_confirmation_flag_exits_before_process_creation(tmp_path: Path) -> None:
    calls: list[str] = []

    def forbidden_factory(config: NativeRunConfig) -> object:
        calls.append("process")
        raise AssertionError("process must not be created")

    exit_code = main(
        ["--broker-exe", str(tmp_path / "missing.exe"), "--output", str(tmp_path / "out.json")],
        process_factory=forbidden_factory,
    )

    assert exit_code == 2
    assert calls == []


@pytest.mark.parametrize("kind", ["outside_publish", "wrong_name"])
def test_broker_path_must_be_an_absolute_published_executable(tmp_path: Path, kind: str) -> None:
    published = tmp_path / "publish"
    published.mkdir()
    broker = (tmp_path / "RuAgent.Broker.exe") if kind == "outside_publish" else (published / "other.exe")
    broker.write_bytes(b"fixture")
    calls: list[str] = []

    exit_code = main(
        [
            "--broker-exe",
            str(broker.resolve()),
            "--output",
            str((tmp_path / "out.json").resolve()),
            "--confirm-native-volume-change",
        ],
        process_factory=lambda config: calls.append("process"),
    )

    assert exit_code == 2
    assert calls == []


def test_bearer_ownership_proves_correct_and_random_wrong_before_ws() -> None:
    calls: list[str] = []

    class Response:
        def __init__(self, status_code: int) -> None:
            self.status_code = status_code

        def close(self) -> None:
            calls.append("closed")

    def get(_url: str, *, headers: dict[str, str], timeout: float) -> Response:
        del timeout
        calls.append(headers["Authorization"])
        return Response(200 if headers["Authorization"] == "Bearer correct" else 401)

    assert prove_bearer_ownership(
        "http://127.0.0.1:12345",
        "correct",
        "wrong",
        timeout_seconds=5,
        http_get=get,
    )
    assert calls[:3:2] == ["Bearer correct", "Bearer wrong"]


def test_protocol_client_uses_authenticated_private_volume_contract() -> None:
    sent_types: list[str] = []
    headers: list[dict[str, str]] = []

    class Socket:
        def __init__(self) -> None:
            self.reply = ""

        async def send(self, raw: str) -> None:
            request = json.loads(raw)
            sent_types.append(request["message_type"])
            payload: dict[str, Any]
            response_type = "tool_result"
            if request["message_type"] == "hello":
                response_type = "hello_ok"
                payload = {
                    "status": "ready",
                    "tools": [
                        {
                            "tool_id": "system.volume",
                            "tool_version": "1.0.0",
                            "scopes": ["system.write"],
                        }
                    ],
                    "skills": [],
                }
            elif request["message_type"] == "session_open":
                response_type = "session_opened"
                payload = {"status": "succeeded", "scopes": ["system.write"], "skill_ids": []}
            elif request["message_type"] == "session_close":
                response_type = "skill_result"
                payload = {"status": "succeeded"}
            else:
                assert request["payload"]["tool_id"] == "system.volume"
                payload = {
                    "status": "succeeded",
                    "output": {"operation": "get", "level": 40, "muted": False},
                    "error_code": None,
                    "safe_summary": "verified",
                    "duration_ms": 1,
                    "verification_state": "verified",
                    "endpoint_role": ROLE,
                    "endpoint_hash": HASH,
                    "adapter_invoked": True,
                }
            self.reply = json.dumps(
                {
                    "protocol_version": 1,
                    "message_type": response_type,
                    "request_id": request["request_id"],
                    "session_id": request["session_id"],
                    "correlation_id": request["correlation_id"],
                    "deadline_ms": request["deadline_ms"],
                    "payload": payload,
                }
            )

        async def recv(self) -> str:
            return self.reply

        async def close(self) -> None:
            return None

    socket = Socket()

    async def ws_connect(_uri: str, **kwargs: Any) -> Socket:
        headers.append(kwargs["additional_headers"])
        return socket

    async def scenario() -> VolumeReceipt:
        client = BrokerProtocolClient(
            "http://127.0.0.1:12345",
            "private-token",
            5,
            websocket_connect=ws_connect,
        )
        await client.open()
        result = await client.get_volume("get-original")
        await client.close()
        return result

    result = asyncio.run(scenario())

    assert result.status == "succeeded"
    assert headers == [{"Authorization": "Bearer private-token"}]
    assert sent_types == ["hello", "session_open", "tool_request", "session_close"]


def test_protocol_client_rejects_mismatched_context() -> None:
    class Socket:
        async def send(self, raw: str) -> None:
            request = json.loads(raw)
            self.reply = json.dumps(
                {
                    "protocol_version": 1,
                    "message_type": "hello_ok",
                    "request_id": "wrong-request",
                    "session_id": request["session_id"],
                    "correlation_id": request["correlation_id"],
                    "payload": {"tools": []},
                }
            )

        async def recv(self) -> str:
            return self.reply

        async def close(self) -> None:
            return None

    async def ws_connect(_uri: str, **_kwargs: Any) -> Socket:
        return Socket()

    async def scenario() -> None:
        client = BrokerProtocolClient(
            "http://127.0.0.1:12345",
            "private-token",
            5,
            websocket_connect=ws_connect,
        )
        await client.open()

    with pytest.raises(NativeAcceptanceError, match="broker_context_mismatch"):
        asyncio.run(scenario())


@pytest.mark.parametrize("failure", [None, "tool", "malformed", TimeoutError(), KeyboardInterrupt()])
def test_original_state_is_restored_on_every_post_capture_outcome(failure: object) -> None:
    client = FakeClient(failure if isinstance(failure, (BaseException, str)) else None)

    result = NativeAcceptanceRunner(client).run_sync()

    assert client.calls == [
        "get-original",
        "change-one-point",
        "verify-change",
        "restore",
        "verify-restore",
    ]
    assert result.restored is True
    assert result.passed is (failure is None)


def test_restore_failure_forces_failed_result_and_artifact() -> None:
    result = NativeAcceptanceRunner(FakeClient("restore")).run_sync()
    artifact = build_native_artifact(
        result,
        cleanup={"process_terminated": True, "descriptor_removed": True, "temporary_root_removed": True},
        commit="abc123",
    )

    assert result.passed is False
    assert result.restored is False
    assert artifact["evidence_class"] == "native"
    assert artifact["summary"]["status"] == "failed"
    restore = next(check for check in artifact["checks"] if check["id"] == "native.volume_restore")
    assert restore["details"]["restored"] is False


def test_native_artifact_never_contains_state_secrets_paths_or_native_text() -> None:
    result = NativeAcceptanceRunner(FakeClient()).run_sync()
    artifact = build_native_artifact(
        result,
        cleanup={"process_terminated": True, "descriptor_removed": True, "temporary_root_removed": True},
        commit="abc123",
    )
    serialized = json.dumps(artifact, ensure_ascii=False).casefold()

    assert artifact["evidence_class"] == "native"
    assert ROLE in serialized
    assert HASH in serialized
    for forbidden in (
        '"level"',
        '"muted"',
        "private-token",
        "descriptor.json",
        "ruagent.broker.exe",
        "device_name",
        '"arguments"',
        '"output"',
        "native exception text",
    ):
        assert forbidden not in serialized


def test_main_waits_for_http_ownership_before_websocket_and_writes_artifact(tmp_path: Path) -> None:
    publish = tmp_path / "publish"
    publish.mkdir()
    broker = publish / "RuAgent.Broker.exe"
    broker.write_bytes(b"fixture")
    output = (tmp_path / "native.json").resolve()
    events: list[str] = []
    written: list[dict[str, object]] = []

    class Process:
        base_url = "http://127.0.0.1:12345"
        token = "private-token"

        def start(self) -> None:
            events.append("process-start")

        def wait_ready(self) -> None:
            events.append("http-ownership")

        def close(self) -> dict[str, bool]:
            events.append("process-close")
            return {
                "process_terminated": True,
                "descriptor_removed": True,
                "temporary_root_removed": True,
            }

    def client_factory(_base_url: str, _token: str, _timeout: float) -> FakeClient:
        events.append("websocket-client")
        return FakeClient()

    def writer(path: Path, artifact: dict[str, object]) -> None:
        assert path == output
        events.append("artifact-write")
        written.append(artifact)

    exit_code = main(
        [
            "--broker-exe",
            str(broker.resolve()),
            "--output",
            str(output),
            "--confirm-native-volume-change",
        ],
        process_factory=lambda _config: Process(),
        protocol_client_factory=client_factory,
        artifact_writer=writer,
    )

    assert exit_code == 0
    assert events == [
        "process-start",
        "http-ownership",
        "websocket-client",
        "process-close",
        "artifact-write",
    ]
    assert written[0]["evidence_class"] == "native"
