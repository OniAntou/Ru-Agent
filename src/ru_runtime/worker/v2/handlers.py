from __future__ import annotations

import asyncio
import concurrent.futures
import contextlib
import threading
from collections.abc import AsyncIterator, Callable, Mapping
from dataclasses import dataclass
from typing import Any, TypeAlias

from .model_step import RuntimeRunner, StepGenerationError, run_model_step
from .protocol import AGENT_STEP_DELTA, AGENT_STEP_DONE, step_to_payload


@dataclass(frozen=True, slots=True)
class StepReply:
    type: str
    payload: Mapping[str, Any]


StepHandler: TypeAlias = Callable[[dict[str, Any], asyncio.Event], AsyncIterator[StepReply]]


@dataclass(frozen=True, slots=True)
class StepDependencies:
    runner: RuntimeRunner
    # The v1 voice/observation adapters consult this shared lock before using
    # the GPU.  Agent v2 generation must hold it for the whole provider call so
    # retiring the v1 generation handler does not silently reintroduce overlap.
    generation_locks: tuple[threading.Lock, ...] = ()


def build_step_handler(dependencies: StepDependencies):
    async def handle(payload: dict[str, Any], cancelled: asyncio.Event) -> AsyncIterator[StepReply]:
        loop = asyncio.get_running_loop()
        queue: asyncio.Queue[StepReply | None] = asyncio.Queue(maxsize=16)

        def publish_delta(text: str) -> None:
            if cancelled.is_set():
                return
            try:
                pending = asyncio.run_coroutine_threadsafe(
                    queue.put(StepReply(AGENT_STEP_DELTA, {"text": text})),
                    loop,
                )
            except RuntimeError:
                return
            while not cancelled.is_set():
                try:
                    pending.result(timeout=0.05)
                    return
                except concurrent.futures.TimeoutError:
                    continue
                except (concurrent.futures.CancelledError, RuntimeError):
                    return
            pending.cancel()

        async def generate() -> None:
            acquired_locks: list[threading.Lock] = []
            try:
                for lock in dependencies.generation_locks:
                    while not lock.acquire(blocking=False):
                        if cancelled.is_set():
                            raise asyncio.CancelledError
                        await asyncio.sleep(0.01)
                    acquired_locks.append(lock)
                step = await run_model_step(
                    dependencies.runner,
                    payload,
                    cancelled,
                    on_delta=publish_delta,
                )
                await queue.put(StepReply(AGENT_STEP_DONE, step_to_payload(step)))
            finally:
                for lock in reversed(acquired_locks):
                    lock.release()
                current_task = asyncio.current_task()
                if current_task is not None and current_task.cancelling():
                    # The consumer is closing, so nobody may be left to drain a
                    # full queue.  Best-effort notification avoids deadlocking
                    # async-generator cleanup.
                    with contextlib.suppress(asyncio.QueueFull):
                        queue.put_nowait(None)
                else:
                    # Cooperative request cancellation must remain attached to
                    # the provider thread until it returns.  The consumer keeps
                    # draining buffered deltas and makes room for this sentinel.
                    await queue.put(None)

        task = asyncio.create_task(generate())
        try:
            while True:
                item = await queue.get()
                if item is None:
                    await task
                    return
                if cancelled.is_set() and item.type != AGENT_STEP_DONE:
                    # Drain already-buffered deltas while the provider observes
                    # the cooperative signal.  Awaiting the producer sentinel
                    # keeps the to_thread call owned until the provider returns.
                    continue
                yield item
        finally:
            cancelled.set()
            if not task.done():
                # A to_thread future cannot stop its underlying thread.  Keep
                # draining bounded output while the provider observes the
                # cancellation predicate, then reap the owned task instead of
                # cancelling its asyncio wrapper and detaching the thread.
                while not task.done():
                    while True:
                        try:
                            queue.get_nowait()
                        except asyncio.QueueEmpty:
                            break
                    await asyncio.wait({task}, timeout=0.05)
            await asyncio.gather(task, return_exceptions=True)

    return handle


def create_runtime_step_handler():
    """Bind v2 to the existing model runner without importing runtime state at import time."""
    from ru_runtime.worker.v1 import runtime_services

    return build_step_handler(
        StepDependencies(
            runtime_services.core.runner,
            generation_locks=(
                runtime_services._TURN_LOCK,
                runtime_services._MEMORY_TURN_LOCK,
            ),
        )
    )


async def run_step_handler(
    runner: RuntimeRunner,
    payload: dict[str, Any],
    cancelled: asyncio.Event,
) -> AsyncIterator[StepReply]:
    async for reply in build_step_handler(StepDependencies(runner))(payload, cancelled):
        yield reply


__all__ = [
    "RuntimeRunner",
    "StepDependencies",
    "StepGenerationError",
    "StepHandler",
    "StepReply",
    "build_step_handler",
    "create_runtime_step_handler",
    "run_model_step",
    "run_step_handler",
]
