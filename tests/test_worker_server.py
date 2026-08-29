from __future__ import annotations

import asyncio
import json
import socket
from collections.abc import AsyncIterator

import pytest
from websockets.asyncio.client import connect
from websockets.exceptions import ConnectionClosed

from ru_runtime.worker.server import parse_args, serve_worker
from ru_runtime.worker.v1.protocol import (
    ERROR,
    HELLO,
    RUNTIME_REQUEST,
    RUNTIME_RESPONSE,
    VOICE_CONTROL,
    VOICE_DONE,
    WORKER_HEALTH,
    encode_envelope,
    validate_envelope,
)
from ru_runtime.worker.v1.server import WorkerHandlers, WorkerReply, serve_runtime_worker


def test_handshake_and_health_are_token_protected() -> None:
    async def scenario() -> None:
        async with (
            running_server(default_handlers(), token="worker-secret") as uri,
            connect(uri) as websocket,
        ):
            await send_hello(websocket, "worker-secret")
            hello = validate_envelope(json.loads(await websocket.recv()))
            assert hello["type"] == "hello.ok"
            assert hello["seq"] == 0
            assert hello["payload"]["version"] == 1
            assert hello["payload"]["capabilities"] == [
                "runtime",
                "voice",
                "vts",
                "observation",
            ]

            await websocket.send(
                encode_envelope({"v": 1, "id": "health-1", "type": WORKER_HEALTH, "payload": {}})
            )
            health = validate_envelope(json.loads(await websocket.recv()))
            assert health["type"] == "worker.health.ok"
            assert health["seq"] == 0
            assert health["payload"]["ready"] is True

    asyncio.run(scenario())


def test_bad_token_closes_before_accepting_work() -> None:
    async def scenario() -> None:
        async with (
            running_server(default_handlers(), token="worker-secret") as uri,
            connect(uri) as websocket,
        ):
            await send_hello(websocket, "wrong-secret")
            with pytest.raises(ConnectionClosed):
                await websocket.recv()
            assert websocket.close_code == 4401

    asyncio.run(scenario())


def test_retired_chat_message_is_rejected_before_dispatch() -> None:
    async def scenario() -> None:
        async with running_server(default_handlers()) as uri, connect(uri) as websocket:
            await send_hello(websocket, "worker-secret")
            await websocket.recv()
            await websocket.send(
                json.dumps(
                    {"v": 1, "id": "retired-1", "type": "chat.start", "payload": {}}
                )
            )
            error = validate_envelope(json.loads(await websocket.recv()))
            assert error["type"] == ERROR
            assert error["payload"] == {"code": "unknown_type", "retryable": False}

    asyncio.run(scenario())


def test_runtime_request_dispatches_one_response_and_binary_body() -> None:
    async def runtime(payload: dict, _cancelled: asyncio.Event) -> AsyncIterator[WorkerReply]:
        assert payload == {
            "method": "POST",
            "path_and_query": "/voice/tts",
            "content_type": "application/json",
            "body": "{}",
        }
        yield WorkerReply(
            RUNTIME_RESPONSE,
            {
                "status_code": 200,
                "content_type": "audio/wav",
                "headers": {},
                "binary": True,
            },
            binary=b"RIFF",
        )

    async def scenario() -> None:
        async with (
            running_server(default_handlers(runtime=runtime)) as uri,
            connect(uri) as websocket,
        ):
            await send_hello(websocket, "worker-secret")
            await websocket.recv()
            await websocket.send(
                encode_envelope(
                    {
                        "v": 1,
                        "id": "runtime-1",
                        "type": RUNTIME_REQUEST,
                        "payload": {
                            "method": "POST",
                            "path_and_query": "/voice/tts",
                            "content_type": "application/json",
                            "body": "{}",
                        },
                    }
                )
            )
            response = validate_envelope(json.loads(await websocket.recv()))
            binary = await websocket.recv()

            assert response["type"] == RUNTIME_RESPONSE
            assert response["seq"] == 0
            assert response["payload"]["byte_length"] == 4
            assert binary == b"RIFF"

    asyncio.run(scenario())


def test_unknown_version_returns_redacted_error_then_closes() -> None:
    async def scenario() -> None:
        async with running_server(default_handlers()) as uri, connect(uri) as websocket:
            await websocket.send(
                json.dumps(
                    {
                        "v": 99,
                        "id": "bad-version",
                        "type": HELLO,
                        "payload": {"token": "must-not-leak"},
                    }
                )
            )
            error = validate_envelope(json.loads(await websocket.recv()))
            assert error["type"] == ERROR
            assert error["payload"] == {
                "code": "unsupported_version",
                "retryable": False,
            }
            with pytest.raises(ConnectionClosed):
                await websocket.recv()
            assert websocket.close_code == 4400

    asyncio.run(scenario())


def test_voice_audio_control_is_immediately_followed_by_non_empty_binary() -> None:
    async def voice(_payload: dict, _cancelled: asyncio.Event) -> AsyncIterator[WorkerReply]:
        yield WorkerReply(
            VOICE_CONTROL,
            {"state": "audio_ready", "byte_length": 4},
            binary=b"RIFF",
        )
        yield WorkerReply(VOICE_DONE, {"state": "done"})

    async def scenario() -> None:
        async with running_server(default_handlers(voice=voice)) as uri, connect(uri) as websocket:
            await send_hello(websocket, "worker-secret")
            await websocket.recv()
            await websocket.send(
                encode_envelope({"v": 1, "id": "voice-1", "type": "voice.start", "payload": {}})
            )
            control = validate_envelope(json.loads(await websocket.recv()))
            audio = await websocket.recv()
            done = validate_envelope(json.loads(await websocket.recv()))

            assert control["type"] == VOICE_CONTROL
            assert control["seq"] == 0
            assert audio == b"RIFF"
            assert done["type"] == VOICE_DONE
            assert done["seq"] == 1

    asyncio.run(scenario())


def test_server_shutdown_closes_connections_and_request_tasks() -> None:
    async def scenario() -> None:
        port = unused_port()
        stop = asyncio.Event()
        ready = asyncio.Event()
        task = asyncio.create_task(
            serve_worker(
                "127.0.0.1",
                port,
                "worker-secret",
                default_handlers(),
                stop_event=stop,
                ready_event=ready,
            )
        )
        await asyncio.wait_for(ready.wait(), timeout=2)
        websocket = await connect(f"ws://127.0.0.1:{port}/worker/v1")
        await send_hello(websocket, "worker-secret")
        await websocket.recv()

        stop.set()
        await asyncio.wait_for(task, timeout=2)
        assert websocket.state.name == "CLOSED"

    asyncio.run(scenario())


def test_runtime_server_binds_the_runtime_handler_factory(monkeypatch) -> None:
    observed = []

    def fake_factory() -> WorkerHandlers:
        observed.append("created")
        return default_handlers()

    monkeypatch.setattr("ru_runtime.worker.v1.handlers.create_runtime_handlers", fake_factory)

    async def scenario() -> None:
        port = unused_port()
        stop = asyncio.Event()
        ready = asyncio.Event()
        task = asyncio.create_task(
            serve_runtime_worker(
                "127.0.0.1",
                port,
                "worker-secret",
                stop_event=stop,
                ready_event=ready,
            )
        )
        await asyncio.wait_for(ready.wait(), timeout=2)
        async with connect(f"ws://127.0.0.1:{port}/worker/v1") as websocket:
            await send_hello(websocket, "worker-secret")
            hello = validate_envelope(json.loads(await websocket.recv()))
            assert hello["type"] == "hello.ok"
        stop.set()
        await asyncio.wait_for(task, timeout=2)

    asyncio.run(scenario())
    assert observed == ["created"]


def test_v1_only_closes_v2_before_constructing_a_v2_handler(monkeypatch) -> None:
    constructed = []

    def forbidden_factory(*_args, **_kwargs):
        constructed.append("v2")
        raise AssertionError("v2 handler must not be constructed in v1-only mode")

    monkeypatch.setattr("ru_runtime.worker.server.create_runtime_step_handler", forbidden_factory, raising=False)

    async def scenario() -> None:
        port = unused_port()
        stop = asyncio.Event()
        ready = asyncio.Event()
        task = asyncio.create_task(
            serve_worker(
                "127.0.0.1",
                port,
                "worker-secret",
                default_handlers(),
                v1_only=True,
                stop_event=stop,
                ready_event=ready,
            )
        )
        await asyncio.wait_for(ready.wait(), timeout=2)
        async with connect(f"ws://127.0.0.1:{port}/worker/v2") as websocket:
            with pytest.raises(ConnectionClosed):
                await websocket.recv()
            assert websocket.close_code == 4404
        stop.set()
        await asyncio.wait_for(task, timeout=2)

    asyncio.run(scenario())
    assert constructed == []


def test_worker_cli_exposes_an_explicit_v1_only_flag() -> None:
    args = parse_args(["--host", "127.0.0.1", "--port", "8123", "--v1-only"])

    assert args.host == "127.0.0.1"
    assert args.port == 8123
    assert args.v1_only is True


class running_server:
    def __init__(self, handlers: WorkerHandlers, token: str = "worker-secret") -> None:
        self.handlers = handlers
        self.token = token
        self.port = unused_port()
        self.stop = asyncio.Event()
        self.ready = asyncio.Event()
        self.task: asyncio.Task | None = None

    async def __aenter__(self) -> str:
        self.task = asyncio.create_task(
            serve_worker(
                "127.0.0.1",
                self.port,
                self.token,
                self.handlers,
                stop_event=self.stop,
                ready_event=self.ready,
            )
        )
        await asyncio.wait_for(self.ready.wait(), timeout=2)
        return f"ws://127.0.0.1:{self.port}/worker/v1"

    async def __aexit__(self, *_exc: object) -> None:
        self.stop.set()
        assert self.task is not None
        await asyncio.wait_for(self.task, timeout=2)


async def empty_handler(
    _payload: dict,
    _cancelled: asyncio.Event,
) -> AsyncIterator[WorkerReply]:
    if False:
        yield WorkerReply(ERROR, {})


def default_handlers(
    *,
    runtime=empty_handler,
    voice=empty_handler,
    vts=empty_handler,
) -> WorkerHandlers:
    return WorkerHandlers(runtime=runtime, voice=voice, vts=vts)


async def send_hello(websocket, token: str) -> None:
    await websocket.send(
        encode_envelope(
            {
                "v": 1,
                "id": "conn-1",
                "type": HELLO,
                "payload": {"token": token, "capabilities": ["runtime", "voice"]},
            }
        )
    )


def unused_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])
