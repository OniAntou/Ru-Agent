from __future__ import annotations

import asyncio
import json
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, replace
from typing import Any, Protocol

from ru_runtime.generation.prompt_context import (
    PromptContextError,
    SystemPromptContext,
    parse_system_prompt_context,
)
from ru_runtime.generation.system_prompt_composer import (
    SystemPromptReceipt,
    SystemPromptSnapshot,
    compose_system_prompt,
)
from ru_runtime.generation.tool_calls import ToolCallParseError, parse_tool_call
from ru_runtime.generation.turns import ConversationTurn
from ru_runtime.worker.v1.action_loop import (
    ActionProtocolError,
    ModelCapability,
    parse_model_capabilities,
    validate_selection,
)

from .protocol import AssistantStep, ModelToolCall, ProtocolError, validate_start_payload

_PUBLIC_STEP_ERROR_CODES = frozenset(
    {
        "invalid_request",
        "provider_timeout",
        "provider_unavailable",
        "protocol_failure",
        "prompt_contract_invalid",
        "generation_failed",
        "invalid_selection",
        "capability_unavailable",
        "worker_unavailable",
    }
)


class RuntimeRunner(Protocol):
    def generate_reply(
        self,
        prompt: str,
        max_new_tokens: int,
        temperature: float,
        top_k: int,
        turns_history: Sequence[ConversationTurn] | None = None,
        memory_context: str | None = None,
        mood_context: str | None = None,
        allow_web_search: bool = False,
        vts_instruction: str = "",
        *,
        on_stream_delta: Callable[[str], None] | None = None,
        on_stream_reset: Callable[[], None] | None = None,
        on_complete_timing: Callable[[float], None] | None = None,
        is_cancelled: Callable[[], bool] | None = None,
        structured_tools_only: bool = False,
        agent_follow_up: bool = False,
        action_capabilities: Sequence[ModelCapability] = (),
        system_prompt_snapshot: SystemPromptSnapshot | None = None,
    ) -> str: ...


class StepGenerationError(RuntimeError):
    """A provider-neutral error code safe for the Worker wire/log boundary."""

    def __init__(self, code: str, *, retryable: bool = False) -> None:
        if code not in _PUBLIC_STEP_ERROR_CODES:
            code = "generation_failed"
            retryable = True
        self._code = code
        self._retryable = retryable
        super().__init__(code)

    @property
    def code(self) -> str:
        return self._code

    @property
    def retryable(self) -> bool:
        return self._retryable


@dataclass(frozen=True, slots=True)
class ModelStepResult:
    """A visible AssistantStep paired with its digest-only prompt receipt."""

    step: AssistantStep
    prompt_receipt: SystemPromptReceipt


async def run_model_step(
    runner: RuntimeRunner,
    payload: Mapping[str, Any],
    cancelled: asyncio.Event,
    *,
    on_delta: Callable[[str], None] | None = None,
) -> ModelStepResult:
    request = _parse_step_request(payload)
    try:
        snapshot = compose_system_prompt(request.prompt_context)
    except PromptContextError:
        raise StepGenerationError("prompt_contract_invalid") from None
    holdback = _DeltaHoldback(sink=on_delta)

    def stream_delta(delta: str) -> None:
        if cancelled.is_set():
            return
        holdback.forward(delta)

    def stream_reset() -> None:
        holdback.reset()

    try:
        raw_reply = await asyncio.to_thread(
            runner.generate_reply,
            prompt=request.prompt,
            max_new_tokens=request.max_tokens,
            temperature=request.temperature,
            top_k=request.top_k,
            turns_history=request.turns,
            allow_web_search=request.allow_web_search,
            system_prompt_snapshot=snapshot,
            on_stream_delta=stream_delta,
            on_stream_reset=stream_reset,
            is_cancelled=cancelled.is_set,
            structured_tools_only=bool(request.capabilities),
            action_capabilities=request.capabilities,
            agent_follow_up=False,
        )
    except asyncio.CancelledError:
        cancelled.set()
        raise
    except Exception as error:
        if cancelled.is_set():
            raise asyncio.CancelledError from None
        if isinstance(error, StepGenerationError):
            raise
        if isinstance(error, TimeoutError):
            raise StepGenerationError("provider_timeout", retryable=True) from None
        if isinstance(error, (ConnectionError, OSError)):
            raise StepGenerationError("provider_unavailable", retryable=True) from None
        if isinstance(error, (TypeError, ValueError, ActionProtocolError, ToolCallParseError)):
            raise StepGenerationError("protocol_failure") from None
        raise StepGenerationError("generation_failed", retryable=True) from None

    if cancelled.is_set():
        raise asyncio.CancelledError
    if not isinstance(raw_reply, str):
        raise StepGenerationError("protocol_failure")
    if len(raw_reply) > 1_000_000:
        raise StepGenerationError("protocol_failure")

    selection = _parse_selection(raw_reply, request.capabilities)
    if selection is not None:
        return ModelStepResult(
            step=AssistantStep(
                text=None,
                tool_calls=(
                    ModelToolCall(
                        provider_call_id=None,
                        tool_id=selection.tool_id,
                        tool_version=selection.tool_version,
                        arguments=selection.arguments,
                    ),
                ),
                finish_reason="tool_calls",
            ),
            prompt_receipt=snapshot.receipt,
        )
    holdback.release()
    return ModelStepResult(
        step=AssistantStep(text=raw_reply, tool_calls=(), finish_reason="stop"),
        prompt_receipt=snapshot.receipt,
    )


generate_model_step = run_model_step


class _StepRequest:
    __slots__ = (
        "prompt",
        "turns",
        "prompt_context",
        "allow_web_search",
        "max_tokens",
        "temperature",
        "top_k",
        "capabilities",
    )

    def __init__(
        self,
        prompt: str,
        turns: tuple[ConversationTurn, ...],
        prompt_context: SystemPromptContext,
        allow_web_search: bool,
        max_tokens: int,
        temperature: float,
        top_k: int,
        capabilities: tuple[ModelCapability, ...],
    ) -> None:
        self.prompt = prompt
        self.turns = turns
        self.prompt_context = prompt_context
        self.allow_web_search = allow_web_search
        self.max_tokens = max_tokens
        self.temperature = temperature
        self.top_k = top_k
        self.capabilities = capabilities


def _parse_step_request(payload: Mapping[str, Any]) -> _StepRequest:
    try:
        validate_start_payload(payload)
    except ProtocolError:
        raise StepGenerationError("invalid_request") from None
    prompt = payload.get("prompt")
    if not isinstance(prompt, str) or not prompt.strip() or len(prompt) > 16_000:
        raise StepGenerationError("invalid_request")
    raw_turns = payload.get("turns", [])
    if not isinstance(raw_turns, list) or len(raw_turns) > 8:
        raise StepGenerationError("invalid_request")
    turns: list[ConversationTurn] = []
    for item in raw_turns:
        if not isinstance(item, Mapping):
            raise StepGenerationError("invalid_request")
        turn_prompt = item.get("prompt")
        turn_response = item.get("response")
        if (
            not isinstance(turn_prompt, str)
            or not isinstance(turn_response, str)
            or not turn_prompt.strip()
            or not turn_response.strip()
            or len(turn_prompt) > 16_000
            or len(turn_response) > 32_000
        ):
            raise StepGenerationError("invalid_request")
        try:
            turns.append(ConversationTurn(turn_prompt, turn_response))
        except ValueError:
            raise StepGenerationError("invalid_request") from None

    try:
        prompt_context = parse_system_prompt_context(payload["system_prompt_context"])
    except (KeyError, PromptContextError):
        raise StepGenerationError("prompt_contract_invalid") from None
    settings = payload.get("settings", {})
    if not isinstance(settings, Mapping):
        raise StepGenerationError("invalid_request")
    raw_tools = settings.get("model_tools", [])
    if not isinstance(raw_tools, list):
        raise StepGenerationError("invalid_request")
    legacy_tools: list[dict[str, Any]] = []
    schemas: list[Mapping[str, Any] | None] = []
    for raw_tool in raw_tools:
        if not isinstance(raw_tool, Mapping):
            raise StepGenerationError("invalid_request")
        legacy_tool = dict(raw_tool)
        schema = legacy_tool.pop("input_schema", None)
        if schema is not None and not isinstance(schema, Mapping):
            raise StepGenerationError("invalid_request")
        legacy_tools.append(legacy_tool)
        schemas.append(schema)
    try:
        parsed_capabilities = parse_model_capabilities({"model_tools": legacy_tools})
    except ActionProtocolError:
        raise StepGenerationError("invalid_request") from None
    capabilities = tuple(
        replace(capability, hint=_tool_prompt_hint(capability.hint, schema))
        for capability, schema in zip(parsed_capabilities, schemas, strict=True)
    )
    requested_web_search = settings.get("allow_web_search", False)
    allow_web_search = requested_web_search and any(
        capability.id == "web.search" and capability.availability == "enabled"
        for capability in prompt_context.capabilities
    )
    return _StepRequest(
        prompt=prompt,
        turns=tuple(turns),
        prompt_context=prompt_context,
        allow_web_search=allow_web_search,
        max_tokens=_bounded_int(settings.get("max_tokens", 256), 1, 1024),
        temperature=_bounded_float(settings.get("temperature", 0.7), 0.1, 2.0),
        top_k=_bounded_int(settings.get("top_k", 50), 1, 200),
        capabilities=capabilities,
    )


def _parse_selection(text: str, capabilities: tuple[ModelCapability, ...]):
    stripped = text.strip()
    if capabilities:
        try:
            return validate_selection(stripped, capabilities)
        except ActionProtocolError as error:
            raise StepGenerationError(error.code) from None
    try:
        call = parse_tool_call(stripped)
    except ToolCallParseError:
        raise StepGenerationError("invalid_selection") from None
    if call is not None:
        raise StepGenerationError("capability_unavailable")
    return None


class _DeltaHoldback:
    def __init__(self, *, sink: Callable[[str], None] | None) -> None:
        self._sink = sink
        self._held: list[str] = []
        self._decided = False
        self._structured = False
        self._visible_emitted = False
        self._bytes = 0

    def forward(self, delta: str) -> None:
        if not isinstance(delta, str) or not delta or self._sink is None:
            return
        if len(delta) > 64 * 1024:
            raise ValueError("delta is out of bounds")
        self._bytes += len(delta.encode("utf-8"))
        if self._bytes > 1_000_000:
            raise ValueError("text is out of bounds")
        if self._decided:
            if not self._structured:
                self._sink(delta)
                self._visible_emitted = True
            return
        self._held.append(delta)
        combined = "".join(self._held)
        if not combined.strip():
            return
        self._decided = True
        if combined.lstrip().startswith(("{", "[")):
            self._structured = True
            return
        for held in self._held:
            self._sink(held)
            self._visible_emitted = True
        self._held.clear()

    def release(self) -> None:
        if self._sink is not None and self._structured:
            for held in self._held:
                self._sink(held)
                self._visible_emitted = True
        self._held.clear()
        self._decided = True
        self._structured = False

    def reset(self) -> None:
        if self._visible_emitted:
            # Worker v2 intentionally has no stream-reset frame.  Once prose
            # reached the client, retrying would duplicate or contradict it;
            # fail this step and let Core decide whether to start a new one.
            raise StepGenerationError("provider_unavailable", retryable=True)
        self._held.clear()
        self._decided = False
        self._structured = False


def _tool_prompt_hint(hint: str, schema: Mapping[str, Any] | None) -> str:
    if schema is None:
        return hint
    rendered = json.dumps(
        dict(schema),
        ensure_ascii=False,
        allow_nan=False,
        sort_keys=True,
        separators=(",", ":"),
    )
    prefix = f"{hint}; " if hint else ""
    return f"{prefix}input_schema={rendered}"


def _bounded_int(value: object, minimum: int, maximum: int) -> int:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise StepGenerationError("invalid_request")
    result = int(value)
    if result != value or not minimum <= result <= maximum:
        raise StepGenerationError("invalid_request")
    return result


def _bounded_float(value: object, minimum: float, maximum: float) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise StepGenerationError("invalid_request")
    result = float(value)
    if not minimum <= result <= maximum:
        raise StepGenerationError("invalid_request")
    return result
