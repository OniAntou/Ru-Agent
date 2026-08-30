"""Protocol-only worker used by deterministic Harness runs.

The production Python worker is retired in the managed-runtime topology.  The
deterministic Harness still needs a tiny, dependency-free WebSocket peer so it
can exercise the real C# gateway and client contracts without loading a model,
audio device, or VTube Studio.  Keep this module under ``scripts``: it is a
test fixture, not a compatibility runtime.
"""

from __future__ import annotations

import asyncio
import contextlib
import hmac
import json
from collections.abc import AsyncIterator, Callable, Mapping
from dataclasses import dataclass
from typing import Any, TypeAlias

from websockets.asyncio.server import ServerConnection, serve
from websockets.exceptions import ConnectionClosed

V1_PROTOCOL_VERSION = 1
V2_PROTOCOL_VERSION = 2

# Worker v1 message types
HELLO = "hello"
HELLO_OK = "hello.ok"
RUNTIME_LOAD = "runtime.load"
RUNTIME_STATE = "runtime.state"
RUNTIME_REQUEST = "runtime.request"
RUNTIME_RESPONSE = "runtime.response"
VOICE_START = "voice.start"
VOICE_CONTROL = "voice.control"
VOICE_DONE = "voice.done"
VTS_CATALOG = "vts.catalog"
VTS_ACTION = "vts.action"
WORKER_HEALTH = "worker.health"
WORKER_HEALTH_OK = "worker.health.ok"
ERROR = "error"

# Worker v2 message types
AGENT_STEP_START = "agent.step.start"
AGENT_STEP_DELTA = "agent.step.delta"
AGENT_STEP_DONE = "agent.step.done"
AGENT_STEP_CANCEL = "agent.step.cancel"

_V1_TYPES = frozenset(
    {
        HELLO,
        HELLO_OK,
        RUNTIME_LOAD,
        RUNTIME_STATE,
        RUNTIME_REQUEST,
        RUNTIME_RESPONSE,
        VOICE_START,
        VOICE_CONTROL,
        VOICE_DONE,
        VTS_CATALOG,
        VTS_ACTION,
        WORKER_HEALTH,
        WORKER_HEALTH_OK,
        ERROR,
    }
)
_V2_TYPES = frozenset(
    {
        HELLO,
        HELLO_OK,
        AGENT_STEP_START,
        AGENT_STEP_DELTA,
        AGENT_STEP_DONE,
        AGENT_STEP_CANCEL,
        ERROR,
    }
)


class ProtocolError(ValueError):
    """Small fixture-side protocol error with a stable public code."""

    def __init__(self, code: str) -> None:
        self.code = code
        super().__init__(code)


@dataclass(frozen=True, slots=True)
class WorkerReply:
    type: str
    payload: Mapping[str, Any]
    binary: bytes | None = None


WorkerPayload = dict[str, Any]
WorkerHandler: TypeAlias = Callable[
    [WorkerPayload, asyncio.Event],
    AsyncIterator[WorkerReply],
]


@dataclass(frozen=True, slots=True)
class WorkerHandlers:
    runtime: WorkerHandler
    voice: WorkerHandler
    vts: WorkerHandler


@dataclass(frozen=True, slots=True)
class StepReply:
    type: str
    payload: Mapping[str, Any]


class StepGenerationError(RuntimeError):
    """Error shape expected by the deterministic Agent v2 fixture."""

    def __init__(self, code: str, retryable: bool = False) -> None:
        self.code = code
        self.retryable = retryable
        super().__init__(code)


V2Handler: TypeAlias = Callable[
    [dict[str, Any], asyncio.Event],
    AsyncIterator[StepReply],
]


def _decode(raw: object) -> dict[str, Any]:
    if not isinstance(raw, str):
        raise ProtocolError("text_control_required")
    try:
        value = json.loads(raw)
    except (json.JSONDecodeError, RecursionError):
        raise ProtocolError("invalid_json") from None
    if not isinstance(value, Mapping):
        raise ProtocolError("invalid_envelope")
    result = dict(value)
    if not isinstance(result.get("id"), str) or not result["id"].strip():
        raise ProtocolError("invalid_id")
    if not isinstance(result.get("type"), str):
        raise ProtocolError("unknown_type")
    if not isinstance(result.get("payload"), Mapping):
        raise ProtocolError("invalid_payload")
    return result


def _encode(
    version: int,
    request_id: str,
    message_type: str,
    payload: Mapping[str, Any],
    *,
    sequence: int | None = None,
    correlation_id: str | None = None,
) -> str:
    envelope: dict[str, Any] = {
        "v": version,
        "id": request_id,
        "type": message_type,
        "payload": dict(payload),
    }
    if sequence is not None:
        envelope["seq"] = sequence
    if correlation_id is not None:
        envelope["correlation_id"] = correlation_id
    return json.dumps(envelope, ensure_ascii=False, separators=(",", ":"))


def _safe_request_id(raw: object) -> str:
    try:
        value = json.loads(raw) if isinstance(raw, str) else None
    except (json.JSONDecodeError, RecursionError):
        value = None
    request_id = value.get("id") if isinstance(value, Mapping) else None
    return request_id if isinstance(request_id, str) and request_id.strip() else "connection"


async def serve_worker(
    host: str,
    port: int,
    token: str,
    handlers: WorkerHandlers,
    *,
    v2_handler: V2Handler | None = None,
    stop_event: asyncio.Event | None = None,
) -> None:
    """Serve both private worker protocol versions for one Harness session."""

    if host != "127.0.0.1":
        raise ValueError("worker host must be 127.0.0.1")
    if not 0 <= port <= 65535:
        raise ValueError("worker port is out of range")
    if not isinstance(token, str) or not token.strip():
        raise ValueError("worker token is required")

    shutdown = stop_event or asyncio.Event()

    async def handle(connection: ServerConnection) -> None:
        path = getattr(getattr(connection, "request", None), "path", "")
        path = path.split("?", 1)[0]
        if path == "/worker/v1":
            await _handle_v1_connection(connection, token, handlers)
        elif path == "/worker/v2":
            await _handle_v2_connection(connection, token, v2_handler)
        else:
            await connection.close(code=4404, reason="worker endpoint not found")

    async with serve(
        handle,
        host,
        port,
        compression=None,
        max_size=8 * 1024 * 1024,
        ping_interval=20,
        ping_timeout=20,
        close_timeout=2,
    ):
        await shutdown.wait()


async def _send(
    connection: ServerConnection,
    lock: asyncio.Lock,
    message: str,
    binary: bytes | None = None,
) -> None:
    async with lock:
        await connection.send(message)
        if binary is not None:
            await connection.send(binary)


async def _send_v1_error(
    connection: ServerConnection,
    lock: asyncio.Lock,
    request_id: str,
    code: str,
    *,
    sequence: int = 0,
    correlation_id: str | None = None,
) -> None:
    await _send(
        connection,
        lock,
        _encode(
            V1_PROTOCOL_VERSION,
            request_id,
            ERROR,
            {"code": code, "retryable": False},
            sequence=sequence,
            correlation_id=correlation_id,
        ),
    )


async def _handle_v1_connection(
    connection: ServerConnection,
    token: str,
    handlers: WorkerHandlers,
) -> None:
    lock = asyncio.Lock()
    request_tasks: set[asyncio.Task[None]] = set()
    capabilities = ["runtime", "voice", "vts", "observation"]
    try:
        first = await connection.recv()
        if not isinstance(first, str):
            await connection.close(code=4400, reason="hello must be text")
            return
        try:
            hello = _decode(first)
        except ProtocolError as error:
            await _send_v1_error(connection, lock, _safe_request_id(first), error.code)
            await connection.close(code=4400, reason=error.code)
            return
        if hello.get("v") != V1_PROTOCOL_VERSION or hello.get("type") != HELLO:
            await _send_v1_error(connection, lock, str(hello.get("id") or "connection"), "hello_required")
            await connection.close(code=4400, reason="hello_required")
            return
        supplied = hello["payload"].get("token")
        if not isinstance(supplied, str) or not hmac.compare_digest(supplied, token):
            await connection.close(code=4401, reason="unauthorized")
            return
        await _send(
            connection,
            lock,
            _encode(
                V1_PROTOCOL_VERSION,
                hello["id"],
                HELLO_OK,
                {"version": V1_PROTOCOL_VERSION, "capabilities": capabilities},
                sequence=0,
            ),
        )

        async for frame in connection:
            if not isinstance(frame, str):
                await _send_v1_error(connection, lock, "connection", "text_control_required")
                continue
            try:
                envelope = _decode(frame)
            except ProtocolError as error:
                await _send_v1_error(connection, lock, _safe_request_id(frame), error.code)
                continue
            if envelope.get("v") != V1_PROTOCOL_VERSION:
                await _send_v1_error(connection, lock, envelope["id"], "unsupported_version")
                continue
            request_id = envelope["id"]
            message_type = envelope["type"]
            if message_type == WORKER_HEALTH:
                await _send(
                    connection,
                    lock,
                    _encode(
                        V1_PROTOCOL_VERSION,
                        request_id,
                        WORKER_HEALTH_OK,
                        {"ready": True, "version": V1_PROTOCOL_VERSION, "capabilities": capabilities},
                        sequence=0,
                    ),
                )
                continue
            handler = _select_handler(message_type, handlers)
            if handler is None:
                await _send_v1_error(connection, lock, request_id, "unsupported_operation")
                continue
            task = asyncio.create_task(
                _run_v1_request(
                    connection,
                    lock,
                    request_id,
                    dict(envelope["payload"]),
                    handler,
                    envelope.get("correlation_id"),
                )
            )
            request_tasks.add(task)
            task.add_done_callback(request_tasks.discard)
    except ConnectionClosed:
        return
    finally:
        for task in request_tasks:
            task.cancel()
        if request_tasks:
            await asyncio.gather(*request_tasks, return_exceptions=True)


def _select_handler(message_type: str, handlers: WorkerHandlers) -> WorkerHandler | None:
    if message_type in {RUNTIME_LOAD, RUNTIME_REQUEST}:
        return handlers.runtime
    if message_type == VOICE_START:
        return handlers.voice
    if message_type in {VTS_CATALOG, VTS_ACTION}:
        return handlers.vts
    return None


async def _run_v1_request(
    connection: ServerConnection,
    lock: asyncio.Lock,
    request_id: str,
    payload: WorkerPayload,
    handler: WorkerHandler,
    correlation_id: str | None,
) -> None:
    cancelled = asyncio.Event()
    sequence = 0
    try:
        async for reply in handler(payload, cancelled):
            response_payload = dict(reply.payload)
            announces_binary = (
                reply.type == VOICE_CONTROL and response_payload.get("state") == "audio_ready"
            ) or (reply.type == RUNTIME_RESPONSE and response_payload.get("binary") is True)
            if announces_binary and not reply.binary:
                raise ProtocolError("missing_binary")
            if reply.binary is not None and not announces_binary:
                raise ProtocolError("unexpected_binary")
            if reply.binary is not None:
                response_payload["byte_length"] = len(reply.binary)
            await _send(
                connection,
                lock,
                _encode(
                    V1_PROTOCOL_VERSION,
                    request_id,
                    reply.type,
                    response_payload,
                    sequence=sequence,
                    correlation_id=correlation_id,
                ),
                reply.binary,
            )
            sequence += 1
    except asyncio.CancelledError:
        with contextlib.suppress(ConnectionClosed):
            await _send_v1_error(connection, lock, request_id, "cancelled", sequence=sequence)
        raise
    except ConnectionClosed:
        return
    except Exception:
        with contextlib.suppress(ConnectionClosed):
            await _send_v1_error(connection, lock, request_id, "worker_failure", sequence=sequence)


async def _handle_v2_connection(
    connection: ServerConnection,
    token: str,
    handler: V2Handler | None,
) -> None:
    lock = asyncio.Lock()
    request_tasks: dict[str, asyncio.Task[None]] = {}
    try:
        first = await connection.recv()
        if not isinstance(first, str):
            await connection.close(code=4400, reason="hello must be text")
            return
        try:
            hello = _decode(first)
        except ProtocolError as error:
            await _send_v2_error(connection, lock, _safe_request_id(first), error.code, 1)
            await connection.close(code=4400, reason=error.code)
            return
        if hello.get("v") != V2_PROTOCOL_VERSION or hello.get("type") != HELLO:
            await _send_v2_error(connection, lock, str(hello.get("id") or "connection"), "hello_required", 1)
            await connection.close(code=4400, reason="hello_required")
            return
        supplied = hello["payload"].get("token")
        if not isinstance(supplied, str) or not hmac.compare_digest(supplied, token):
            await connection.close(code=4401, reason="unauthorized")
            return
        await _send(
            connection,
            lock,
            _encode(
                V2_PROTOCOL_VERSION,
                hello["id"],
                HELLO_OK,
                {"version": V2_PROTOCOL_VERSION, "capabilities": ["agent.step"]},
                sequence=0,
            ),
        )

        async for frame in connection:
            if not isinstance(frame, str):
                await _send_v2_error(connection, lock, "connection", "text_control_required", 1)
                continue
            try:
                envelope = _decode(frame)
            except ProtocolError as error:
                await _send_v2_error(connection, lock, _safe_request_id(frame), error.code, 1)
                continue
            if envelope.get("v") != V2_PROTOCOL_VERSION:
                await _send_v2_error(connection, lock, envelope["id"], "unsupported_version", 1)
                continue
            request_id = envelope["id"]
            message_type = envelope["type"]
            if message_type == AGENT_STEP_CANCEL:
                task = request_tasks.pop(request_id, None)
                if task is None:
                    await _send_v2_error(connection, lock, request_id, "request_not_found", 1)
                    continue
                task.cancel()
                continue
            if message_type != AGENT_STEP_START:
                await _send_v2_error(connection, lock, request_id, "unsupported_operation", 1)
                continue
            if request_tasks:
                await _send_v2_error(connection, lock, request_id, "request_in_progress", 1)
                continue
            task = asyncio.create_task(
                _run_v2_request(
                    connection,
                    lock,
                    request_id,
                    dict(envelope["payload"]),
                    envelope.get("correlation_id"),
                    handler,
                )
            )
            request_tasks[request_id] = task
            task.add_done_callback(lambda completed, key=request_id: request_tasks.pop(key, None))
    except ConnectionClosed:
        return
    finally:
        for task in request_tasks.values():
            task.cancel()
        if request_tasks:
            await asyncio.gather(*request_tasks.values(), return_exceptions=True)


async def _send_v2_error(
    connection: ServerConnection,
    lock: asyncio.Lock,
    request_id: str,
    code: str,
    sequence: int,
    correlation_id: str | None = None,
) -> None:
    await _send(
        connection,
        lock,
        _encode(
            V2_PROTOCOL_VERSION,
            request_id,
            ERROR,
            {"code": code, "retryable": False},
            sequence=sequence,
            correlation_id=correlation_id,
        ),
    )


async def _run_v2_request(
    connection: ServerConnection,
    lock: asyncio.Lock,
    request_id: str,
    payload: dict[str, Any],
    correlation_id: str | None,
    handler: V2Handler | None,
) -> None:
    sequence = 1
    try:
        if handler is None:
            raise StepGenerationError("worker_unavailable", retryable=True)
        cancelled = asyncio.Event()
        async for reply in handler(payload, cancelled):
            if reply.type not in {AGENT_STEP_DELTA, AGENT_STEP_DONE}:
                raise StepGenerationError("protocol_failure")
            await _send(
                connection,
                lock,
                _encode(
                    V2_PROTOCOL_VERSION,
                    request_id,
                    reply.type,
                    dict(reply.payload),
                    sequence=sequence,
                    correlation_id=correlation_id,
                ),
            )
            sequence += 1
            if reply.type == AGENT_STEP_DONE:
                return
        raise StepGenerationError("protocol_failure")
    except asyncio.CancelledError:
        with contextlib.suppress(ConnectionClosed):
            await _send_v2_error(
                connection,
                lock,
                request_id,
                "cancelled",
                sequence,
                correlation_id,
            )
    except StepGenerationError as error:
        with contextlib.suppress(ConnectionClosed):
            await _send(
                connection,
                lock,
                _encode(
                    V2_PROTOCOL_VERSION,
                    request_id,
                    ERROR,
                    {"code": error.code, "retryable": error.retryable},
                    sequence=sequence,
                    correlation_id=correlation_id,
                ),
            )
    except ConnectionClosed:
        return
    except Exception:
        with contextlib.suppress(ConnectionClosed):
            await _send_v2_error(
                connection,
                lock,
                request_id,
                "generation_failed",
                sequence,
                correlation_id,
            )


__all__ = [
    "AGENT_STEP_CANCEL",
    "AGENT_STEP_DELTA",
    "AGENT_STEP_DONE",
    "AGENT_STEP_START",
    "ERROR",
    "RUNTIME_LOAD",
    "RUNTIME_REQUEST",
    "RUNTIME_RESPONSE",
    "RUNTIME_STATE",
    "StepGenerationError",
    "StepReply",
    "VOICE_CONTROL",
    "VOICE_DONE",
    "VOICE_START",
    "VTS_ACTION",
    "VTS_CATALOG",
    "WorkerHandlers",
    "WorkerReply",
    "serve_worker",
]
