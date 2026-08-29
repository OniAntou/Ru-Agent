from __future__ import annotations

import argparse
import asyncio
import contextlib
import hashlib
import hmac
import json
import logging
import os
from collections.abc import AsyncIterator, Callable, Mapping
from functools import partial
from typing import TYPE_CHECKING, Any

from websockets.asyncio.server import ServerConnection, serve
from websockets.exceptions import ConnectionClosed

from ru_runtime.persistence.diagnostic_log import (
    get_diagnostic_log_writer,
    install_diagnostic_logging_handler,
)
from ru_runtime.worker.v1.server import WorkerHandlers
from ru_runtime.worker.v1.server import handle_connection as handle_v1_connection
from ru_runtime.worker.v2.protocol import (
    AGENT_STEP_CANCEL,
    AGENT_STEP_DELTA,
    AGENT_STEP_DONE,
    AGENT_STEP_START,
    ERROR,
    HELLO,
    HELLO_OK,
    MAX_FRAME_BYTES,
    PROTOCOL_VERSION,
    ProtocolError,
    decode_envelope,
    encode_envelope,
    is_safe_error_code,
    is_safe_request_id,
)

logger = logging.getLogger(__name__)
if TYPE_CHECKING:
    from ru_runtime.worker.v2.handlers import StepReply
else:
    StepReply = Any
V2Handler = Callable[[dict[str, Any], asyncio.Event], AsyncIterator[StepReply]]
_MAX_REQUESTS_PER_CONNECTION = 1024


async def serve_worker(
    host: str,
    port: int,
    token: str,
    handlers: WorkerHandlers,
    *,
    v2_handler: V2Handler | None = None,
    v2_runner: Any = None,
    stop_event: asyncio.Event | None = None,
    ready_event: asyncio.Event | None = None,
    v1_only: bool = False,
) -> None:
    if host != "127.0.0.1":
        raise ValueError("worker host must be 127.0.0.1")
    if port < 0 or port > 65535:
        raise ValueError("worker port is out of range")
    if not isinstance(token, str) or not token.strip():
        raise ValueError("worker token is required")
    if not v1_only:
        if v2_handler is not None and v2_runner is not None:
            raise ValueError("choose one v2 handler")
        if v2_handler is None and v2_runner is not None:
            from ru_runtime.worker.v2.handlers import StepDependencies, build_step_handler

            v2_handler = build_step_handler(StepDependencies(v2_runner))
    install_diagnostic_logging_handler(get_diagnostic_log_writer())
    shutdown = stop_event or asyncio.Event()

    async def handle(connection: ServerConnection) -> None:
        request_path = getattr(getattr(connection, "request", None), "path", "")
        path = request_path.split("?", 1)[0]
        if path == "/worker/v1":
            await handle_v1_connection(connection, token, handlers)
        elif path == "/worker/v2":
            if v1_only:
                await connection.close(code=4404, reason="worker endpoint not found")
                return
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
    v1_only: bool = False,
) -> None:
    from ru_runtime.worker.v1.handlers import create_runtime_handlers
    from ru_runtime.worker.v1.runtime_services import _apply_settings_on_startup, _shutdown_runtime

    v2_handler = None
    if not v1_only:
        from ru_runtime.worker.v2.handlers import create_runtime_step_handler

        v2_handler = create_runtime_step_handler()

    _apply_settings_on_startup()
    try:
        await serve_worker(
            host,
            port,
            token,
            create_runtime_handlers(),
            v2_handler=v2_handler,
            stop_event=stop_event,
            ready_event=ready_event,
            v1_only=v1_only,
        )
    finally:
        from ru_runtime.generation.search import shutdown_search_executor

        try:
            shutdown_search_executor()
        except Exception:
            logger.exception("Failed to shut down search executor")
        try:
            _shutdown_runtime()
        except Exception:
            logger.exception("Failed to shut down runtime services")


async def _handle_v2_connection(
    connection: ServerConnection,
    token: str,
    handler: V2Handler | None,
) -> None:
    send_lock = asyncio.Lock()
    request_tasks: dict[str, asyncio.Task[None]] = {}
    cancellation_events: dict[str, asyncio.Event] = {}
    seen_request_ids: set[str] = set()
    next_sequences: dict[str, int] = {}

    def take_sequence(request_id: str) -> int:
        sequence = next_sequences.get(request_id, 1)
        next_sequences[request_id] = sequence + 1
        return sequence

    try:
        try:
            first_frame = await connection.recv()
        except ConnectionClosed:
            return
        if not isinstance(first_frame, str):
            await connection.close(code=4400, reason="hello must be text")
            return
        if _frame_is_oversized(first_frame):
            await _send_error(
                connection,
                send_lock,
                "connection",
                "message_too_large",
                False,
                take_sequence("connection"),
            )
            await connection.close(code=4409, reason="message_too_large")
            return
        try:
            hello = decode_envelope(first_frame)
        except (json.JSONDecodeError, ProtocolError) as error:
            code = error.code if isinstance(error, ProtocolError) else "invalid_json"
            request_id = _safe_request_id(first_frame)
            await _send_error(
                connection,
                send_lock,
                request_id,
                code,
                False,
                take_sequence(request_id),
            )
            await connection.close(code=4400, reason=code)
            return
        if hello["type"] != HELLO:
            await _send_error(
                connection,
                send_lock,
                hello["id"],
                "hello_required",
                False,
                take_sequence(hello["id"]),
            )
            await connection.close(code=4400, reason="hello_required")
            return
        supplied_token = hello["payload"].get("token")
        if not isinstance(supplied_token, str) or not hmac.compare_digest(supplied_token, token):
            await connection.close(code=4401, reason="unauthorized")
            return
        await _send_control(
            connection,
            send_lock,
            {
                "v": PROTOCOL_VERSION,
                "id": hello["id"],
                "type": HELLO_OK,
                "seq": 0,
                "payload": {"version": PROTOCOL_VERSION, "capabilities": ["agent.step"]},
            },
        )
        async for frame in connection:
            if not isinstance(frame, str):
                await _send_error(
                    connection,
                    send_lock,
                    "connection",
                    "text_control_required",
                    False,
                    take_sequence("connection"),
                )
                continue
            if _frame_is_oversized(frame):
                await _send_error(
                    connection,
                    send_lock,
                    "connection",
                    "message_too_large",
                    False,
                    take_sequence("connection"),
                )
                await connection.close(code=4409, reason="message_too_large")
                return
            try:
                envelope = decode_envelope(frame)
            except (json.JSONDecodeError, ProtocolError) as error:
                code = error.code if isinstance(error, ProtocolError) else "invalid_json"
                safe_id = _safe_request_id(frame)
                await _send_error(
                    connection,
                    send_lock,
                    safe_id,
                    code,
                    False,
                    take_sequence(safe_id),
                )
                continue
            request_id = envelope["id"]
            message_type = envelope["type"]
            if message_type == AGENT_STEP_CANCEL:
                cancelled = cancellation_events.get(request_id)
                task = request_tasks.get(request_id)
                if cancelled is None or task is None:
                    await _send_error(
                        connection,
                        send_lock,
                        request_id,
                        "request_not_found",
                        False,
                        take_sequence(request_id),
                    )
                    continue
                cancelled.set()
                continue
            if message_type != AGENT_STEP_START:
                await _send_error(
                    connection,
                    send_lock,
                    request_id,
                    "unsupported_operation",
                    False,
                    take_sequence(request_id),
                )
                continue
            if request_id in seen_request_ids:
                await _send_error(
                    connection,
                    send_lock,
                    request_id,
                    "duplicate_request",
                    False,
                    take_sequence(request_id),
                )
                continue
            if request_tasks:
                await _send_error(
                    connection,
                    send_lock,
                    request_id,
                    "request_in_progress",
                    False,
                    take_sequence(request_id),
                )
                continue
            if len(seen_request_ids) >= _MAX_REQUESTS_PER_CONNECTION:
                await _send_error(
                    connection,
                    send_lock,
                    request_id,
                    "request_limit_reached",
                    False,
                    take_sequence(request_id),
                )
                continue
            seen_request_ids.add(request_id)
            cancelled = asyncio.Event()
            cancellation_events[request_id] = cancelled
            task = asyncio.create_task(
                _run_v2_request(
                    connection,
                    send_lock,
                    request_id,
                    envelope["payload"],
                    envelope.get("correlation_id"),
                    handler,
                    cancelled,
                    take_sequence,
                )
            )
            request_tasks[request_id] = task
            task.add_done_callback(
                partial(_remove_completed, request_tasks, cancellation_events, request_id)
            )
    except ConnectionClosed:
        pass
    finally:
        for event in cancellation_events.values():
            event.set()
        if request_tasks:
            await asyncio.gather(*request_tasks.values(), return_exceptions=True)


async def _run_v2_request(
    connection: ServerConnection,
    send_lock: asyncio.Lock,
    request_id: str,
    payload: Mapping[str, Any],
    correlation_id: str | None,
    handler: V2Handler | None,
    cancelled: asyncio.Event,
    take_sequence: Callable[[str], int],
) -> None:
    from ru_runtime.worker.v2.model_step import StepGenerationError

    final_sent = False
    replies: AsyncIterator[StepReply] | None = None
    try:
        if handler is None:
            raise StepGenerationError("worker_unavailable", retryable=True)
        replies = handler(dict(payload), cancelled)
        async for reply in replies:
            if reply.type not in {AGENT_STEP_DELTA, AGENT_STEP_DONE}:
                raise StepGenerationError("protocol_failure")
            if cancelled.is_set() and reply.type != AGENT_STEP_DONE:
                raise asyncio.CancelledError
            envelope: dict[str, Any] = {
                "v": PROTOCOL_VERSION,
                "id": request_id,
                "type": reply.type,
                "seq": take_sequence(request_id),
                "payload": dict(reply.payload),
            }
            if correlation_id:
                envelope["correlation_id"] = correlation_id
            try:
                encoded = encode_envelope(envelope)
            except ProtocolError:
                raise StepGenerationError("protocol_failure") from None
            async with send_lock:
                await connection.send(encoded)
            if reply.type == AGENT_STEP_DONE:
                final_sent = True
                break
        if not final_sent:
            if cancelled.is_set():
                raise asyncio.CancelledError
            raise StepGenerationError("protocol_failure")
    except asyncio.CancelledError:
        with contextlib.suppress(ConnectionClosed):
            await _send_error(
                connection,
                send_lock,
                request_id,
                "cancelled",
                False,
                take_sequence(request_id),
                correlation_id,
            )
    except StepGenerationError as error:
        logger.warning(
            "worker_v2_failed code=%s correlation_ref=%s",
            error.code,
            _correlation_log_ref(correlation_id),
        )
        with contextlib.suppress(ConnectionClosed):
            await _send_error(
                connection,
                send_lock,
                request_id,
                error.code,
                error.retryable,
                take_sequence(request_id),
                correlation_id,
            )
    except ConnectionClosed:
        return
    except Exception:
        logger.warning(
            "worker_v2_failed code=generation_failed correlation_ref=%s",
            _correlation_log_ref(correlation_id),
        )
        with contextlib.suppress(ConnectionClosed):
            await _send_error(
                connection,
                send_lock,
                request_id,
                "generation_failed",
                True,
                take_sequence(request_id),
                correlation_id,
            )
    finally:
        close = getattr(replies, "aclose", None)
        if close is not None:
            try:
                await close()
            except Exception:
                logger.warning(
                    "worker_v2_failed code=handler_cleanup_failed correlation_ref=%s",
                    _correlation_log_ref(correlation_id),
                )


async def _send_error(
    connection: ServerConnection,
    send_lock: asyncio.Lock,
    request_id: str,
    code: str,
    retryable: bool,
    sequence: int,
    correlation_id: str | None = None,
) -> None:
    if not is_safe_request_id(request_id):
        request_id = "connection"
    if not is_safe_error_code(code):
        code = "generation_failed"
        retryable = True
    envelope: dict[str, Any] = {
        "v": PROTOCOL_VERSION,
        "id": request_id,
        "type": ERROR,
        "seq": sequence,
        "payload": {"code": code, "retryable": retryable},
    }
    if correlation_id:
        envelope["correlation_id"] = correlation_id
    await _send_control(connection, send_lock, envelope)


async def _send_control(
    connection: ServerConnection,
    send_lock: asyncio.Lock,
    envelope: Mapping[str, Any],
) -> None:
    async with send_lock:
        await connection.send(encode_envelope(envelope))


def _remove_completed(
    tasks: dict[str, asyncio.Task[None]],
    cancellation_events: dict[str, asyncio.Event],
    request_id: str,
    task: asyncio.Task[None],
) -> None:
    if tasks.get(request_id) is task:
        tasks.pop(request_id, None)
        cancellation_events.pop(request_id, None)


def _safe_request_id(raw: str) -> str:
    try:
        value = json.loads(raw)
    except (json.JSONDecodeError, RecursionError):
        return "connection"
    request_id = value.get("id") if isinstance(value, dict) else None
    return (
        request_id
        if isinstance(request_id, str) and is_safe_request_id(request_id)
        else "connection"
    )


def _frame_is_oversized(frame: str) -> bool:
    if len(frame) > MAX_FRAME_BYTES:
        return True
    return len(frame.encode("utf-8")) > MAX_FRAME_BYTES


def _correlation_log_ref(correlation_id: str | None) -> str:
    if correlation_id is None:
        return "none"
    return hashlib.sha256(correlation_id.encode("ascii")).hexdigest()[:12]


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Ru private protocol worker")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8005)
    parser.add_argument("--v1-only", action="store_true", help="serve only the private Worker v1 device boundary")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    token = os.environ.get("RU_WORKER_TOKEN", "").strip()
    if not token:
        raise SystemExit("RU_WORKER_TOKEN is required")
    try:
        asyncio.run(serve_runtime_worker(args.host, args.port, token, v1_only=args.v1_only))
    except KeyboardInterrupt:
        return 130
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
