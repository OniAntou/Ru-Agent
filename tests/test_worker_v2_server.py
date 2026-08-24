from __future__ import annotations

import asyncio
import json
import logging
import socket
import threading
import time

import pytest
from websockets.asyncio.client import connect
from websockets.exceptions import ConnectionClosed

from ru_runtime.worker.server import serve_worker
from ru_runtime.worker.v1.protocol import encode_envelope as encode_v1
from ru_runtime.worker.v1.protocol import validate_envelope as validate_v1
from ru_runtime.worker.v1.server import WorkerHandlers, WorkerReply
from ru_runtime.worker.v2.handlers import StepReply
from ru_runtime.worker.v2.protocol import (
    AGENT_STEP_CANCEL,
    AGENT_STEP_DONE,
    AGENT_STEP_START,
    ERROR,
    HELLO,
    HELLO_OK,
    encode_envelope,
    validate_envelope,
)


class FakeRunner:
    def __init__(self, reply: str = "Em đã kiểm tra.") -> None:
        self.reply = reply
        self.calls: list[dict] = []

    def generate_reply(self, **kwargs) -> str:
        self.calls.append(kwargs)
        callback = kwargs.get("on_stream_delta")
        if callback is not None:
            callback(self.reply)
        return self.reply


async def empty_handler(_payload: dict, _cancelled: asyncio.Event):
    if False:
        yield WorkerReply("never", {})


def v1_handlers() -> WorkerHandlers:
    return WorkerHandlers(
        chat=empty_handler,
        memory_extract=empty_handler,
        runtime=empty_handler,
        voice=empty_handler,
        vts=empty_handler,
    )


class running_server:
    def __init__(self, runner: FakeRunner | None = None, *, v2_handler=None) -> None:
        self.port = unused_port()
        self.stop = asyncio.Event()
        self.ready = asyncio.Event()
        self.runner = runner
        self.v2_handler = v2_handler
        self.task: asyncio.Task | None = None

    async def __aenter__(self) -> str:
        options = (
            {"v2_handler": self.v2_handler}
            if self.v2_handler is not None
            else {"v2_runner": self.runner}
        )
        self.task = asyncio.create_task(
            serve_worker(
                "127.0.0.1",
                self.port,
                "worker-secret",
                v1_handlers(),
                stop_event=self.stop,
                ready_event=self.ready,
                **options,
            )
        )
        await asyncio.wait_for(self.ready.wait(), timeout=2)
        return f"ws://127.0.0.1:{self.port}"

    async def __aexit__(self, *_exc: object) -> None:
        self.stop.set()
        assert self.task is not None
        await asyncio.wait_for(self.task, timeout=2)


def hello_v2() -> str:
    return encode_envelope(
        {"v": 2, "id": "conn-1", "type": HELLO, "payload": {"token": "worker-secret"}}
    )


def start_v2() -> str:
    return encode_envelope(
        {
            "v": 2,
            "id": "step-1",
            "type": AGENT_STEP_START,
            "correlation_id": "corr-1",
            "payload": {
                "prompt": "Đọc README",
                "turns": [],
                "memory_context": None,
                "settings": {"model_tools": []},
            },
        }
    )


def test_authenticated_v2_hello_emits_versioned_step_done() -> None:
    async def scenario() -> None:
        runner = FakeRunner()
        async with running_server(runner) as uri, connect(uri + "/worker/v2") as websocket:
            await websocket.send(hello_v2())
            hello = validate_envelope(json.loads(await websocket.recv()))
            assert hello["type"] == HELLO_OK
            assert hello["payload"]["version"] == 2

            await websocket.send(start_v2())
            events = [validate_envelope(json.loads(await websocket.recv())) for _ in range(2)]
            assert [event["type"] for event in events] == ["agent.step.delta", AGENT_STEP_DONE]
            assert [event["seq"] for event in events] == [1, 2]
            assert events[-1]["payload"]["text"] == "Em đã kiểm tra."
            assert events[-1]["correlation_id"] == "corr-1"
            assert len(runner.calls) == 1

    asyncio.run(scenario())


def test_v2_rejects_chat_tool_result_without_affecting_v1_chat_route() -> None:
    async def scenario() -> None:
        async with running_server(FakeRunner()) as uri:
            async with connect(uri + "/worker/v2") as websocket:
                await websocket.send(hello_v2())
                await websocket.recv()
                await websocket.send(
                    json.dumps({"v": 2, "id": "step-1", "type": "chat.tool.result", "payload": {}})
                )
                error = validate_envelope(json.loads(await websocket.recv()))
                assert error["type"] == ERROR
                assert error["payload"]["code"] == "unsupported_message"

            async with connect(uri + "/worker/v1") as websocket:
                await websocket.send(
                    encode_v1(
                        {
                            "v": 1,
                            "id": "conn-1",
                            "type": "hello",
                            "payload": {"token": "worker-secret"},
                        }
                    )
                )
                hello = validate_v1(json.loads(await websocket.recv()))
                assert hello["type"] == "hello.ok"

    asyncio.run(scenario())


def test_wrong_v2_token_closes_before_work() -> None:
    async def scenario() -> None:
        async with running_server(FakeRunner()) as uri, connect(uri + "/worker/v2") as websocket:
            await websocket.send(
                encode_envelope(
                    {"v": 2, "id": "conn-1", "type": HELLO, "payload": {"token": "wrong"}}
                )
            )
            with pytest.raises(ConnectionClosed):
                await websocket.recv()
            assert websocket.close_code == 4401

    asyncio.run(scenario())


def test_malformed_hello_id_returns_a_bounded_connection_error() -> None:
    async def scenario() -> None:
        async with running_server(FakeRunner()) as uri, connect(uri + "/worker/v2") as websocket:
            await websocket.send(
                json.dumps(
                    {
                        "v": 2,
                        "id": "   ",
                        "type": HELLO,
                        "payload": {"token": "worker-secret"},
                    }
                )
            )
            error = validate_envelope(json.loads(await websocket.recv()))
            assert error["id"] == "connection"
            assert error["payload"] == {"code": "invalid_id", "retryable": False}

    asyncio.run(scenario())


def test_non_ascii_hello_token_fails_as_protocol_input_without_crashing_auth() -> None:
    async def scenario() -> None:
        async with running_server(FakeRunner()) as uri, connect(uri + "/worker/v2") as websocket:
            await websocket.send(
                json.dumps(
                    {
                        "v": 2,
                        "id": "conn-1",
                        "type": HELLO,
                        "payload": {"token": "worker-sécret"},
                    }
                )
            )
            error = validate_envelope(json.loads(await websocket.recv()))
            assert error["payload"] == {"code": "invalid_hello", "retryable": False}
            with pytest.raises(ConnectionClosed):
                await websocket.recv()
            assert websocket.close_code == 4400

    asyncio.run(scenario())


def test_duplicate_json_fields_are_rejected_instead_of_using_last_value() -> None:
    async def scenario() -> None:
        async with running_server(FakeRunner()) as uri, connect(uri + "/worker/v2") as websocket:
            await websocket.send(
                '{"v":1,"v":2,"id":"conn-1","type":"hello","payload":{"token":"worker-secret"}}'
            )
            error = validate_envelope(json.loads(await websocket.recv()))
            assert error["type"] == ERROR
            assert error["payload"] == {"code": "duplicate_field", "retryable": False}

    asyncio.run(scenario())


def test_deeply_nested_json_is_bounded_before_and_after_hello() -> None:
    nesting = 5_000
    deeply_nested = ("[" * nesting) + "0" + ("]" * nesting)

    async def scenario() -> None:
        async with running_server(FakeRunner()) as uri:
            async with connect(uri + "/worker/v2") as websocket:
                await websocket.send(
                    '{"v":2,"id":"conn-deep","type":"hello","payload":{"token":'
                    + deeply_nested
                    + "}}"
                )
                error = validate_envelope(json.loads(await websocket.recv()))
                assert error["id"] == "connection"
                assert error["seq"] == 1
                assert error["payload"] == {"code": "invalid_json", "retryable": False}

            async with connect(uri + "/worker/v2") as websocket:
                await websocket.send(hello_v2())
                await websocket.recv()
                await websocket.send(
                    '{"v":2,"id":"step-deep","type":"agent.step.start","payload":'
                    + deeply_nested
                    + "}"
                )
                error = validate_envelope(json.loads(await websocket.recv()))
                assert error["id"] == "connection"
                assert error["seq"] == 1
                assert error["payload"] == {"code": "invalid_json", "retryable": False}

    asyncio.run(scenario())


def test_completed_request_id_cannot_start_a_second_generation() -> None:
    async def scenario() -> None:
        runner = FakeRunner()
        async with running_server(runner) as uri, connect(uri + "/worker/v2") as websocket:
            await websocket.send(hello_v2())
            await websocket.recv()
            await websocket.send(start_v2())
            await websocket.recv()
            await websocket.recv()
            await asyncio.sleep(0)

            await websocket.send(start_v2())
            error = validate_envelope(json.loads(await websocket.recv()))
            assert error["type"] == ERROR
            assert error["payload"]["code"] == "duplicate_request"
            assert error["seq"] == 3
            assert len(runner.calls) == 1

    asyncio.run(scenario())


def test_repeated_request_errors_use_positive_monotonic_sequences() -> None:
    async def scenario() -> None:
        async with running_server(FakeRunner()) as uri, connect(uri + "/worker/v2") as websocket:
            await websocket.send(hello_v2())
            await websocket.recv()
            unsupported = encode_envelope(
                {"v": 2, "id": "bad-1", "type": HELLO, "payload": {"token": "worker-secret"}}
            )
            await websocket.send(unsupported)
            await websocket.send(unsupported)

            errors = [validate_envelope(json.loads(await websocket.recv())) for _ in range(2)]
            assert [error["seq"] for error in errors] == [1, 2]
            assert {error["payload"]["code"] for error in errors} == {"unsupported_operation"}

    asyncio.run(scenario())


def test_handler_that_returns_without_a_final_step_gets_protocol_error() -> None:
    async def scenario() -> None:
        async def no_final_handler(_payload: dict, _cancelled: asyncio.Event):
            if False:
                yield StepReply(AGENT_STEP_DONE, {})

        async with (
            running_server(v2_handler=no_final_handler) as uri,
            connect(uri + "/worker/v2") as websocket,
        ):
            await websocket.send(hello_v2())
            await websocket.recv()
            await websocket.send(start_v2())

            error = validate_envelope(json.loads(await websocket.recv()))
            assert error["type"] == ERROR
            assert error["seq"] == 1
            assert error["payload"] == {"code": "protocol_failure", "retryable": False}

    asyncio.run(scenario())


def test_server_explicitly_closes_handler_generator_after_first_final() -> None:
    async def scenario() -> None:
        cleaned = asyncio.Event()
        retained = []

        async def replies():
            try:
                yield StepReply(
                    AGENT_STEP_DONE,
                    {"text": "done", "tool_calls": [], "finish_reason": "stop"},
                )
                yield StepReply(
                    AGENT_STEP_DONE,
                    {"text": "duplicate", "tool_calls": [], "finish_reason": "stop"},
                )
            finally:
                cleaned.set()

        def retained_handler(_payload: dict, _cancelled: asyncio.Event):
            iterator = replies()
            retained.append(iterator)
            return iterator

        async with (
            running_server(v2_handler=retained_handler) as uri,
            connect(uri + "/worker/v2") as websocket,
        ):
            await websocket.send(hello_v2())
            await websocket.recv()
            await websocket.send(start_v2())

            final = validate_envelope(json.loads(await websocket.recv()))
            assert final["type"] == AGENT_STEP_DONE
            assert final["payload"]["text"] == "done"
            await asyncio.wait_for(cleaned.wait(), timeout=2)

        assert len(retained) == 1

    asyncio.run(scenario())


def test_oversized_v2_frame_is_rejected_before_json_dispatch() -> None:
    async def scenario() -> None:
        async with running_server(FakeRunner()) as uri, connect(uri + "/worker/v2") as websocket:
            await websocket.send(hello_v2())
            await websocket.recv()
            await websocket.send(
                json.dumps(
                    {
                        "v": 2,
                        "id": "step-large",
                        "type": AGENT_STEP_START,
                        "payload": {"prompt": "x", "junk": "x" * (5 * 1024 * 1024)},
                    }
                )
            )
            error = validate_envelope(json.loads(await websocket.recv()))
            assert error["id"] == "connection"
            assert error["payload"]["code"] == "message_too_large"

    asyncio.run(scenario())


def test_provider_exception_is_redacted_to_stable_code_and_correlation_only(caplog) -> None:
    class ExplodingRunner(FakeRunner):
        def generate_reply(self, **kwargs) -> str:
            self.calls.append(kwargs)
            raise RuntimeError(
                "provider body https://secret.example/?token=credential prompt=private prompt"
            )

    async def scenario() -> None:
        async with (
            running_server(ExplodingRunner()) as uri,
            connect(uri + "/worker/v2") as websocket,
        ):
            await websocket.send(hello_v2())
            await websocket.recv()
            await websocket.send(start_v2())
            error = validate_envelope(json.loads(await websocket.recv()))
            assert error["type"] == ERROR
            assert error["payload"] == {"code": "generation_failed", "retryable": True}
            assert error["correlation_id"] == "corr-1"
            assert "secret.example" not in json.dumps(error)

    with caplog.at_level(logging.WARNING, logger="ru_runtime.worker.server"):
        asyncio.run(scenario())
    assert "secret.example" not in caplog.text
    assert "credential" not in caplog.text
    assert "private prompt" not in caplog.text
    assert "corr-1" not in caplog.text


def test_cancel_cancels_the_single_generation_and_emits_terminal_error() -> None:
    class BlockingRunner(FakeRunner):
        def __init__(self) -> None:
            super().__init__("unused")
            self.started = threading.Event()

        def generate_reply(self, **kwargs) -> str:
            self.calls.append(kwargs)
            self.started.set()
            while not kwargs["is_cancelled"]():
                time.sleep(0.002)
            raise RuntimeError("cancelled")

    async def scenario() -> None:
        runner = BlockingRunner()
        async with running_server(runner) as uri, connect(uri + "/worker/v2") as websocket:
            await websocket.send(hello_v2())
            await websocket.recv()
            await websocket.send(start_v2())
            await asyncio.to_thread(runner.started.wait, 2)
            await websocket.send(
                encode_envelope({"v": 2, "id": "step-1", "type": AGENT_STEP_CANCEL, "payload": {}})
            )
            error = validate_envelope(json.loads(await websocket.recv()))
            assert error["type"] == ERROR
            assert error["payload"]["code"] == "cancelled"
            assert len(runner.calls) == 1

    asyncio.run(scenario())


def test_cancel_waits_for_the_provider_thread_to_finish_before_terminal_error() -> None:
    class DelayedCancellationRunner(FakeRunner):
        def __init__(self) -> None:
            super().__init__("unused")
            self.started = threading.Event()
            self.cancel_seen = threading.Event()
            self.release = threading.Event()

        def generate_reply(self, **kwargs) -> str:
            self.calls.append(kwargs)
            self.started.set()
            while not kwargs["is_cancelled"]():
                time.sleep(0.002)
            self.cancel_seen.set()
            self.release.wait(timeout=2)
            return "late reply"

    async def scenario() -> None:
        runner = DelayedCancellationRunner()
        async with running_server(runner) as uri, connect(uri + "/worker/v2") as websocket:
            await websocket.send(hello_v2())
            await websocket.recv()
            await websocket.send(start_v2())
            assert await asyncio.to_thread(runner.started.wait, 2)
            await websocket.send(
                encode_envelope({"v": 2, "id": "step-1", "type": AGENT_STEP_CANCEL, "payload": {}})
            )
            assert await asyncio.to_thread(runner.cancel_seen.wait, 2)

            receive = asyncio.create_task(websocket.recv())
            with pytest.raises(TimeoutError):
                await asyncio.wait_for(asyncio.shield(receive), timeout=0.05)

            runner.release.set()
            error = validate_envelope(json.loads(await asyncio.wait_for(receive, timeout=2)))
            assert error["payload"]["code"] == "cancelled"
            assert len(runner.calls) == 1

    asyncio.run(scenario())


def test_disconnect_keeps_request_owned_until_cooperative_handler_finishes() -> None:
    async def scenario() -> None:
        started = asyncio.Event()
        cancel_seen = asyncio.Event()
        release = asyncio.Event()
        cleaned = asyncio.Event()

        async def delayed_handler(_payload: dict, cancelled: asyncio.Event):
            started.set()
            try:
                await cancelled.wait()
                cancel_seen.set()
                await release.wait()
                raise asyncio.CancelledError
            finally:
                cleaned.set()
            if False:
                yield

        async with running_server(v2_handler=delayed_handler) as uri:
            websocket = await connect(uri + "/worker/v2")
            await websocket.send(hello_v2())
            await websocket.recv()
            await websocket.send(start_v2())
            await asyncio.wait_for(started.wait(), timeout=2)

            await websocket.close()
            await asyncio.wait_for(cancel_seen.wait(), timeout=2)
            await asyncio.sleep(0.05)
            assert not cleaned.is_set()

            release.set()
            await asyncio.wait_for(cleaned.wait(), timeout=2)

    asyncio.run(scenario())


def unused_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])
