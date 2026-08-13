from __future__ import annotations

import asyncio
import argparse
import contextlib
import hmac
import json
import os
from collections.abc import AsyncIterator, Callable, Mapping
from dataclasses import dataclass
from typing import Any, TypeAlias

from websockets.asyncio.server import ServerConnection, serve
from websockets.exceptions import ConnectionClosed

from ru_runtime.worker_protocol import (
    CHAT_CANCEL,
    CHAT_START,
    ERROR,
    HELLO,
    HELLO_OK,
    MEMORY_EXTRACT,
    RUNTIME_LOAD,
    RUNTIME_REQUEST,
    RUNTIME_RESPONSE,
    PROTOCOL_VERSION,
    ProtocolError,
    VOICE_CONTROL,
    VOICE_START,
    VTS_ACTION,
    VTS_CATALOG,
    WORKER_HEALTH,
    WORKER_HEALTH_OK,
    encode_envelope,
    validate_envelope,
)

WorkerPayload = dict[str, Any]


@dataclass(frozen=True, slots=True)
class WorkerReply:
    type: str
    payload: Mapping[str, Any]
    binary: bytes | None = None


WorkerHandler: TypeAlias = Callable[
    [WorkerPayload, asyncio.Event],
    AsyncIterator[WorkerReply],
]


@dataclass(frozen=True, slots=True)
class WorkerHandlers:
    chat: WorkerHandler
    memory_extract: WorkerHandler
    runtime: WorkerHandler
    voice: WorkerHandler
    vts: WorkerHandler
    observation: WorkerHandler


async def serve_worker(
    host: str,
    port: int,
    token: str,
    handlers: WorkerHandlers,
    *,
    stop_event: asyncio.Event | None = None,
    ready_event: asyncio.Event | None = None,
) -> None:
    if host != "127.0.0.1":
        raise ValueError("worker host must be 127.0.0.1")
    if port < 0 or port > 65535:
        raise ValueError("worker port is out of range")
    if not isinstance(token, str) or not token.strip():
        raise ValueError("worker token is required")

    shutdown = stop_event or asyncio.Event()

    async def handle(connection: ServerConnection) -> None:
        await _handle_connection(connection, token, handlers)

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
        if ready_event is not None:
            ready_event.set()
        await shutdown.wait()


async def serve_runtime_worker(
    host: str,
    port: int,
    token: str,
    *,
    stop_event: asyncio.Event | None = None,
    ready_event: asyncio.Event | None = None,
) -> None:
    """Start the protocol server with the existing private runtime adapters."""
    from ru_runtime.worker_runtime_handlers import create_runtime_handlers

    await serve_worker(
        host,
        port,
        token,
        create_runtime_handlers(),
        stop_event=stop_event,
        ready_event=ready_event,
    )


async def _handle_connection(
    connection: ServerConnection,
    token: str,
    handlers: WorkerHandlers,
) -> None:
    request_path = getattr(getattr(connection, "request", None), "path", "")
    if request_path.split("?", 1)[0] != "/worker/v1":
        await connection.close(code=4404, reason="worker endpoint not found")
        return

    send_lock = asyncio.Lock()
    request_tasks: dict[str, asyncio.Task[None]] = {}
    cancellation_events: dict[str, asyncio.Event] = {}

    try:
        try:
            first_frame = await connection.recv()
        except ConnectionClosed:
            return

        if not isinstance(first_frame, str):
            await connection.close(code=4400, reason="hello must be text")
            return

        try:
            hello = validate_envelope(json.loads(first_frame))
        except (json.JSONDecodeError, ProtocolError) as error:
            code = error.code if isinstance(error, ProtocolError) else "invalid_json"
            await _send_error(connection, send_lock, _safe_request_id(first_frame), code, False)
            await connection.close(code=4400, reason=code)
            return

        if hello["type"] != HELLO:
            await _send_error(connection, send_lock, hello["id"], "hello_required", False)
            await connection.close(code=4400, reason="hello_required")
            return

        supplied_token = hello["payload"].get("token")
        if not isinstance(supplied_token, str) or not hmac.compare_digest(supplied_token, token):
            await connection.close(code=4401, reason="unauthorized")
            return

        capabilities = [
            "chat",
            "memory.extract",
            "runtime",
            "voice",
            "vts",
            "observation",
        ]
        await _send_control(
            connection,
            send_lock,
            {
                "v": PROTOCOL_VERSION,
                "id": hello["id"],
                "type": HELLO_OK,
                "seq": 0,
                "payload": {
                    "version": PROTOCOL_VERSION,
                    "capabilities": capabilities,
                },
            },
        )

        async for frame in connection:
            if not isinstance(frame, str):
                await _send_error(connection, send_lock, "connection", "text_control_required", False)
                continue

            try:
                envelope = validate_envelope(json.loads(frame))
            except (json.JSONDecodeError, ProtocolError) as error:
                code = error.code if isinstance(error, ProtocolError) else "invalid_json"
                await _send_error(connection, send_lock, _safe_request_id(frame), code, False)
                continue

            request_id = envelope["id"]
            message_type = envelope["type"]
            if message_type == WORKER_HEALTH:
                await _send_control(
                    connection,
                    send_lock,
                    {
                        "v": PROTOCOL_VERSION,
                        "id": request_id,
                        "type": WORKER_HEALTH_OK,
                        "seq": 0,
                        "payload": {
                            "ready": True,
                            "version": PROTOCOL_VERSION,
                            "capabilities": capabilities,
                        },
                    },
                )
                continue

            if message_type == CHAT_CANCEL:
                task = request_tasks.get(request_id)
                cancelled = cancellation_events.get(request_id)
                if cancelled is not None:
                    cancelled.set()
                if task is not None and not task.done():
                    task.cancel()
                    with contextlib.suppress(asyncio.CancelledError, ConnectionClosed):
                        await task
                else:
                    await _send_error(connection, send_lock, request_id, "request_not_found", False)
                continue

            handler = _select_handler(message_type, handlers)
            if handler is None:
                await _send_error(connection, send_lock, request_id, "unsupported_operation", False)
                continue
            if request_id in request_tasks and not request_tasks[request_id].done():
                await _send_error(connection, send_lock, request_id, "duplicate_request", False)
                continue

            cancelled = asyncio.Event()
            cancellation_events[request_id] = cancelled
            task = asyncio.create_task(
                _run_request(
                    connection,
                    send_lock,
                    request_id,
                    dict(envelope["payload"]),
                    handler,
                    cancelled,
                )
            )
            request_tasks[request_id] = task

            def remove_completed(
                completed: asyncio.Task[None],
                *,
                completed_id: str = request_id,
            ) -> None:
                if request_tasks.get(completed_id) is completed:
                    request_tasks.pop(completed_id, None)
                    cancellation_events.pop(completed_id, None)

            task.add_done_callback(remove_completed)
    except ConnectionClosed:
        pass
    finally:
        for cancelled in cancellation_events.values():
            cancelled.set()
        for task in request_tasks.values():
            task.cancel()
        if request_tasks:
            await asyncio.gather(*request_tasks.values(), return_exceptions=True)


async def _run_request(
    connection: ServerConnection,
    send_lock: asyncio.Lock,
    request_id: str,
    payload: WorkerPayload,
    handler: WorkerHandler,
    cancelled: asyncio.Event,
) -> None:
    sequence = 0
    try:
        async for reply in handler(payload, cancelled):
            if cancelled.is_set():
                raise asyncio.CancelledError

            response_payload = dict(reply.payload)
            binary = reply.binary
            announces_binary = (
                reply.type == VOICE_CONTROL
                and response_payload.get("state") == "audio_ready"
            ) or (
                reply.type == RUNTIME_RESPONSE
                and response_payload.get("binary") is True
            )
            if announces_binary and not binary:
                raise ValueError("announced binary requires non-empty payload")
            if binary is not None and (not binary or not announces_binary):
                raise ValueError("binary frame requires an announcing control")
            if binary is not None:
                response_payload["byte_length"] = len(binary)

            envelope = {
                "v": PROTOCOL_VERSION,
                "id": request_id,
                "type": reply.type,
                "seq": sequence,
                "payload": response_payload,
            }
            async with send_lock:
                await connection.send(encode_envelope(envelope))
                if binary is not None:
                    await connection.send(binary)
            sequence += 1
    except asyncio.CancelledError:
        with contextlib.suppress(ConnectionClosed):
            await _send_error(connection, send_lock, request_id, "cancelled", False, sequence)
        raise
    except ConnectionClosed:
        return
    except Exception:
        with contextlib.suppress(ConnectionClosed):
            await _send_error(connection, send_lock, request_id, "worker_failure", True, sequence)


def _select_handler(message_type: str, handlers: WorkerHandlers) -> WorkerHandler | None:
    if message_type == CHAT_START:
        return handlers.chat
    if message_type == MEMORY_EXTRACT:
        return handlers.memory_extract
    if message_type in (RUNTIME_LOAD, RUNTIME_REQUEST):
        return handlers.runtime
    if message_type == VOICE_START:
        return handlers.voice
    if message_type in (VTS_CATALOG, VTS_ACTION):
        return handlers.vts
    return None


async def _send_error(
    connection: ServerConnection,
    send_lock: asyncio.Lock,
    request_id: str,
    code: str,
    retryable: bool,
    sequence: int = 0,
) -> None:
    await _send_control(
        connection,
        send_lock,
        {
            "v": PROTOCOL_VERSION,
            "id": request_id,
            "type": ERROR,
            "seq": sequence,
            "payload": {"code": code, "retryable": retryable},
        },
    )


async def _send_control(
    connection: ServerConnection,
    send_lock: asyncio.Lock,
    envelope: Mapping[str, Any],
) -> None:
    async with send_lock:
        await connection.send(encode_envelope(envelope))


def _safe_request_id(raw: str) -> str:
    try:
        value = json.loads(raw)
    except json.JSONDecodeError:
        return "connection"
    request_id = value.get("id") if isinstance(value, dict) else None
    return request_id if isinstance(request_id, str) and 0 < len(request_id) <= 128 else "connection"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Ru private protocol worker")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8001)
    args = parser.parse_args(argv)
    token = os.environ.get("RU_WORKER_TOKEN", "").strip()
    if not token:
        parser.error("RU_WORKER_TOKEN is required")
    try:
        asyncio.run(serve_runtime_worker(args.host, args.port, token))
    except KeyboardInterrupt:
        return 130
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
