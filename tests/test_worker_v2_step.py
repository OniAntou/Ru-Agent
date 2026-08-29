from __future__ import annotations

import asyncio
import threading
from hashlib import sha256

import pytest

from ru_runtime.worker.v2.handlers import StepDependencies, build_step_handler, run_model_step
from ru_runtime.worker.v2.model_step import StepGenerationError
from ru_runtime.worker.v2.protocol import AGENT_STEP_DELTA, AGENT_STEP_DONE


class FakeRunner:
    def __init__(self, reply: str = "Em đã kiểm tra.") -> None:
        self.reply = reply
        self.calls: list[dict] = []
        self.started = asyncio.Event()
        self.release = asyncio.Event()

    def generate_reply(self, **kwargs) -> str:
        self.calls.append(kwargs)
        self.started_loop_callback = kwargs.get("on_stream_delta")
        self.started.set()
        if self.reply == "__wait__":
            while not kwargs["is_cancelled"]():
                import time

                time.sleep(0.005)
            raise RuntimeError("provider secret prompt should not leak")
        callback = kwargs.get("on_stream_delta")
        if callback is not None:
            callback(self.reply)
        return self.reply


def step_payload() -> dict:
    return {
        "prompt": "Đọc README",
        "turns": [],
        "system_prompt_context": prompt_context_payload(),
        "settings": {
            "max_tokens": 128,
            "temperature": 0.4,
            "top_k": 20,
            "allow_web_search": False,
            "model_tools": [
                {
                    "tool_id": "files.read",
                    "tool_version": "1.0.0",
                    "scopes": ["workspace.read"],
                    "availability": "executable",
                    "model_enabled": True,
                    "capability_class": "tool",
                    "verification_required": False,
                    "input_schema": {
                        "type": "object",
                        "additionalProperties": False,
                        "properties": {"path": {"type": "string"}},
                        "required": ["path"],
                    },
                }
            ],
        },
    }


def prompt_context_payload() -> dict:
    instruction = "root guidance"
    return {
        "schema_version": 1,
        "agent_profile": "ru-agent",
        "preset": "standard",
        "environment": {
            "model": "test-model",
            "platform": "windows",
            "locale": "vi-VN",
            "timezone": "Asia/Bangkok",
            "date": "2026-08-29",
            "workspace_label": "LLM",
        },
        "instruction_blocks": [
            {
                "source_id": "root:Ru.md",
                "display_name": "Ru.md",
                "content": instruction,
                "content_sha256": sha256(instruction.encode()).hexdigest(),
            }
        ],
        "capabilities": [
            {
                "id": "files.read",
                "version": "1.0.0",
                "summary": "Read a file",
                "input_schema_digest": "a" * 64,
                "policy_summary": "workspace.read",
                "availability": "enabled",
            }
        ],
        "runtime_context": {
            "memory": None,
            "mood": None,
            "voice": None,
            "vts": None,
            "hardware": None,
            "references": [],
        },
    }


def minimal_step_payload() -> dict:
    return {
        "prompt": "x",
        "turns": [],
        "system_prompt_context": prompt_context_payload(),
        "settings": {"model_tools": []},
    }


def test_one_step_calls_runner_once_and_converts_selection_to_one_tool_call() -> None:
    async def scenario() -> None:
        runner = FakeRunner(
            '{"tool_id":"files.read","tool_version":"1.0.0","arguments":{"path":"README.md"}}'
        )
        step = await run_model_step(runner, step_payload(), asyncio.Event())

        assert len(runner.calls) == 1
        assert step.step.text is None
        assert len(step.step.tool_calls) == 1
        assert step.step.tool_calls[0].tool_id == "files.read"
        assert step.step.finish_reason == "tool_calls"
        assert step.prompt_receipt.composer_version == "1.0.0"
        assert "UNTRUSTED DATA" in runner.calls[0]["system_prompt_snapshot"].text
        assert '"required":["path"]' in runner.calls[0]["action_capabilities"][0].hint

    asyncio.run(scenario())


def test_runtime_step_handler_waits_for_and_releases_generation_locks() -> None:
    async def scenario() -> None:
        generation_lock = threading.Lock()
        generation_lock.acquire()
        runner = FakeRunner("Em đã kiểm tra.")
        cancelled = asyncio.Event()
        stream = build_step_handler(
            StepDependencies(runner, generation_locks=(generation_lock,))
        )(minimal_step_payload(), cancelled)

        pending = asyncio.create_task(anext(stream))
        await asyncio.sleep(0.03)
        assert runner.calls == []

        generation_lock.release()
        first = await asyncio.wait_for(pending, timeout=2)
        assert first.type == AGENT_STEP_DELTA
        done = await asyncio.wait_for(anext(stream), timeout=2)
        assert done.type == AGENT_STEP_DONE
        with pytest.raises(StopAsyncIteration):
            await anext(stream)
        assert not generation_lock.locked()

    asyncio.run(scenario())


def test_prose_is_text_and_braced_selection_never_reaches_delta_sink() -> None:
    async def scenario() -> None:
        prose_deltas: list[str] = []
        prose_runner = FakeRunner("Em đã kiểm tra.")
        prose = await run_model_step(
            prose_runner,
            {**step_payload(), "settings": {"model_tools": []}},
            asyncio.Event(),
            on_delta=prose_deltas.append,
        )
        assert prose.step.text == "Em đã kiểm tra."
        assert prose_deltas == ["Em đã kiểm tra."]

        selection_deltas: list[str] = []
        selection_runner = FakeRunner(
            '{"tool_id":"files.read","tool_version":"1.0.0","arguments":{"path":"README.md"}}'
        )
        await run_model_step(
            selection_runner,
            step_payload(),
            asyncio.Event(),
            on_delta=selection_deltas.append,
        )
        assert selection_deltas == []

    asyncio.run(scenario())


def test_tool_selection_without_capability_never_reaches_delta_sink() -> None:
    async def scenario() -> None:
        runner = FakeRunner(
            '{"tool_id":"files.read","tool_version":"1.0.0","arguments":{"path":"README.md"}}'
        )
        deltas: list[str] = []
        payload = {**step_payload(), "settings": {"model_tools": []}}

        with pytest.raises(StepGenerationError, match="capability_unavailable"):
            await run_model_step(runner, payload, asyncio.Event(), on_delta=deltas.append)

        assert deltas == []

    asyncio.run(scenario())


def test_malformed_structured_array_never_reaches_delta_sink() -> None:
    async def scenario() -> None:
        runner = FakeRunner('[{"tool_id":"files.read"}]')
        deltas: list[str] = []

        with pytest.raises(StepGenerationError, match="invalid_selection"):
            await run_model_step(runner, step_payload(), asyncio.Event(), on_delta=deltas.append)

        assert deltas == []

    asyncio.run(scenario())


def test_retry_after_visible_partial_delta_fails_instead_of_corrupting_stream() -> None:
    class ResettingRunner:
        def generate_reply(self, **kwargs) -> str:
            kwargs["on_stream_delta"]("partial")
            kwargs["on_stream_reset"]()
            raise AssertionError("retry output must not be emitted")

    async def scenario() -> None:
        deltas: list[str] = []
        with pytest.raises(StepGenerationError, match="provider_unavailable") as captured:
            await run_model_step(
                ResettingRunner(),
                minimal_step_payload(),
                asyncio.Event(),
                on_delta=deltas.append,
            )

        assert captured.value.retryable is True
        assert deltas == ["partial"]

    asyncio.run(scenario())


def test_step_generation_error_code_cannot_be_mutated_after_validation() -> None:
    error = StepGenerationError("provider_timeout", retryable=True)

    with pytest.raises(AttributeError):
        error.code = "secret.provider_message"

    assert error.code == "provider_timeout"
    assert error.retryable is True


def test_structured_retry_resets_do_not_reset_the_per_step_delta_bound() -> None:
    class RepeatedStructuredResetRunner:
        def generate_reply(self, **kwargs) -> str:
            for _ in range(16):
                kwargs["on_stream_delta"]("{" + ("x" * ((64 * 1024) - 1)))
                kwargs["on_stream_reset"]()
            return "unreachable"

    async def scenario() -> None:
        deltas: list[str] = []
        with pytest.raises(StepGenerationError, match="protocol_failure"):
            await run_model_step(
                RepeatedStructuredResetRunner(),
                minimal_step_payload(),
                asyncio.Event(),
                on_delta=deltas.append,
            )

        assert deltas == []

    asyncio.run(scenario())


def test_cancellation_signal_stops_single_generation_without_detaching_it() -> None:
    async def scenario() -> None:
        runner = FakeRunner("__wait__")
        cancelled = asyncio.Event()
        task = asyncio.create_task(run_model_step(runner, step_payload(), cancelled))
        await asyncio.wait_for(runner.started.wait(), timeout=2)
        cancelled.set()
        with pytest.raises(asyncio.CancelledError):
            await asyncio.wait_for(task, timeout=2)
        assert len(runner.calls) == 1

    asyncio.run(scenario())


def test_cancellation_with_queued_delta_waits_for_provider_thread() -> None:
    class QueuedDeltaRunner:
        def __init__(self) -> None:
            self.second_delta_queued = threading.Event()
            self.cancel_seen = threading.Event()
            self.release = threading.Event()
            self.finished = threading.Event()

        def generate_reply(self, **kwargs) -> str:
            callback = kwargs["on_stream_delta"]
            callback("first")
            callback("second")
            self.second_delta_queued.set()
            while not kwargs["is_cancelled"]():
                import time

                time.sleep(0.002)
            self.cancel_seen.set()
            self.release.wait(timeout=2)
            self.finished.set()
            return "late reply"

    async def scenario() -> None:
        runner = QueuedDeltaRunner()
        cancelled = asyncio.Event()
        stream = build_step_handler(StepDependencies(runner))(minimal_step_payload(), cancelled)
        first = await anext(stream)
        assert first.payload == {"text": "first"}
        assert await asyncio.to_thread(runner.second_delta_queued.wait, 2)

        cancelled.set()
        assert await asyncio.to_thread(runner.cancel_seen.wait, 2)
        next_item = asyncio.create_task(anext(stream))
        with pytest.raises(TimeoutError):
            await asyncio.wait_for(asyncio.shield(next_item), timeout=0.05)
        assert not runner.finished.is_set()

        runner.release.set()
        with pytest.raises(asyncio.CancelledError):
            await asyncio.wait_for(next_item, timeout=2)
        assert runner.finished.is_set()

    asyncio.run(scenario())


def test_completed_step_wins_a_late_cancel_after_generation_finishes() -> None:
    class CompletingRunner:
        def __init__(self) -> None:
            self.release = threading.Event()
            self.finished = threading.Event()

        def generate_reply(self, **kwargs) -> str:
            kwargs["on_stream_delta"]("visible")
            self.release.wait(timeout=2)
            self.finished.set()
            return "visible"

    async def scenario() -> None:
        runner = CompletingRunner()
        cancelled = asyncio.Event()
        stream = build_step_handler(StepDependencies(runner))(minimal_step_payload(), cancelled)
        assert (await anext(stream)).type == AGENT_STEP_DELTA

        runner.release.set()
        assert await asyncio.to_thread(runner.finished.wait, 2)
        await asyncio.sleep(0.05)
        cancelled.set()

        final = await anext(stream)
        assert final.type == AGENT_STEP_DONE
        assert final.payload["text"] == "visible"
        with pytest.raises(StopAsyncIteration):
            await anext(stream)

    asyncio.run(scenario())


def test_delta_bridge_backpressures_a_bursting_provider() -> None:
    class BurstRunner:
        def __init__(self) -> None:
            self.emitted = 0
            self.finished = threading.Event()

        def generate_reply(self, **kwargs) -> str:
            callback = kwargs["on_stream_delta"]
            for _ in range(10_000):
                if kwargs["is_cancelled"]():
                    break
                callback("x")
                self.emitted += 1
            self.finished.set()
            return "x" * self.emitted

    async def scenario() -> None:
        runner = BurstRunner()
        cancelled = asyncio.Event()
        stream = build_step_handler(StepDependencies(runner))(minimal_step_payload(), cancelled)
        first = await anext(stream)
        assert first.type == AGENT_STEP_DELTA

        await asyncio.sleep(0.05)
        assert runner.emitted <= 32
        assert not runner.finished.is_set()

        cancelled.set()
        await stream.aclose()

    asyncio.run(scenario())
