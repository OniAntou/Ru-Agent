from __future__ import annotations

import asyncio
import contextlib
import json
import math
import threading
import uuid
from collections.abc import AsyncIterator, Callable, Mapping
from dataclasses import dataclass
from functools import partial
from typing import Any, ContextManager, Protocol

from ru_runtime.generation.chat import ChatTurn
from ru_runtime.generation.tool_calls import ToolCallParseError, parse_tool_call
from ru_runtime.memory.learning import extract_facts_with_llm
from ru_runtime.worker.v1.http import RuntimeHttpResponse
from ru_runtime.voice.stream import VoiceStreamEvent
from ru_runtime.worker.v1.protocol import (
    CHAT_CHUNK,
    CHAT_DONE,
    CHAT_RESET,
    CHAT_TOOL_REQUEST,
    ERROR,
    MEMORY_FACTS,
    RUNTIME_RESPONSE,
    RUNTIME_STATE,
    VOICE_CONTROL,
    VOICE_DONE,
    VTS_ACTION,
    VTS_CATALOG,
)
from ru_runtime.worker.v1.server import WorkerHandlers, WorkerReply


class RuntimeRunner(Protocol):
    backend: str

    def generate_reply(self, **kwargs: Any) -> str: ...


VoiceAudio = Callable[[str, str, float, str], bytes]
VoiceControl = Callable[[str, str], Mapping[str, Any]]
VoiceStream = Callable[[dict[str, Any], asyncio.Event], AsyncIterator[VoiceStreamEvent]]
VtsManagerFactory = Callable[[], Any]
GenerationScope = Callable[[], ContextManager[Any]]
RuntimeLoader = Callable[[dict[str, Any]], Mapping[str, Any]]


RuntimeRequest = Callable[[str, str, str | None, str], RuntimeHttpResponse]
_RUNTIME_TRANSITION_LOCK = threading.RLock()

_RUNTIME_BACKENDS = (
    "OpenCode Zen",
    "Kilo Gateway",
    "Ollama",
    "LM Studio",
    "Custom OpenAI",
)


def _runtime_load_params(payload: dict[str, Any]) -> dict[str, Any]:
    backend = str(payload.get("backend") or "")
    if backend not in _RUNTIME_BACKENDS:
        raise ValueError("unsupported runtime backend")
    model_name = ""
    base_url: str | None = None
    if backend == "OpenCode Zen":
        model_name = str(payload.get("opencode_model") or "")
    elif backend == "Kilo Gateway":
        model_name = str(payload.get("kilo_model") or "")
    elif backend == "Ollama":
        model_name = str(payload.get("ollama_model") or "")
        base_url = str(payload.get("ollama_url") or "") or None
    elif backend == "LM Studio":
        model_name = str(payload.get("lmstudio_model") or "")
        base_url = str(payload.get("lmstudio_url") or "") or None
    else:
        model_name = str(payload.get("custom_model") or "")
        base_url = str(payload.get("custom_url") or "") or None
    api_variant = str(payload.get("api_variant") or "")
    reasoning = payload.get("kilo_reasoning_enabled", False)
    if not isinstance(reasoning, bool):
        raise ValueError("invalid reasoning setting")
    return {
        "backend": backend,
        "model_name": model_name,
        "base_url": base_url,
        "api_variant": api_variant,
        "kilo_reasoning_enabled": reasoning,
    }


from ru_runtime.vts.emotion import extract_reply_emotion

@dataclass(frozen=True, slots=True)
class RuntimeDependencies:
    runner: RuntimeRunner
    generation_scope: GenerationScope = contextlib.nullcontext
    turn_locks: GenerationScope = contextlib.nullcontext
    voice_audio: VoiceAudio | None = None
    voice_control: VoiceControl | None = None
    voice_stream: VoiceStream | None = None
    vts_manager: VtsManagerFactory | None = None
    runtime_load: RuntimeLoader | None = None
    runtime_request: RuntimeRequest | None = None
    mood_tracker: Any = None


def build_runtime_handlers(dependencies: RuntimeDependencies) -> WorkerHandlers:
    return WorkerHandlers(
        chat=partial(_chat_handler, dependencies),
        memory_extract=partial(_memory_handler, dependencies),
        runtime=partial(_runtime_handler, dependencies),
        voice=partial(_voice_handler, dependencies),
        vts=partial(_vts_handler, dependencies),
    )


def create_runtime_handlers() -> WorkerHandlers:
    """Bind the private protocol to the existing model and device runtimes.

    Imports stay lazy so protocol-only tests never initialize model, memory,
    credential, voice, or VTube Studio state.
    """
    from ru_runtime.worker.v1 import runtime_services as shared
    from ru_runtime.worker.v1.http import dispatch_runtime_request
    from ru_runtime.worker.v1.voice_bridge import RuntimeVoiceBridge

    voice_bridge = RuntimeVoiceBridge(shared)

    def runtime_request(
        method: str,
        path_and_query: str,
        content_type: str | None,
        body: str,
    ) -> RuntimeHttpResponse:
        control = voice_bridge.handle_private_control(method, path_and_query, body)
        if control is not None:
            return control
        return dispatch_runtime_request(method, path_and_query, content_type, body)

    def generation_scope() -> ContextManager[Any]:
        return contextlib.nullcontext()

    def runtime_load(payload: dict[str, Any]) -> Mapping[str, Any]:
        runner = shared.core.runner
        params = _runtime_load_params(payload)
        backend = params["backend"]

        with _RUNTIME_TRANSITION_LOCK:
            previous = {
                "backend": runner.backend,
                "openai_runtime": runner.openai_runtime,
                "kilo_reasoning_enabled": runner.kilo_reasoning_enabled,
                "load_error": runner.load_error,
            }
            try:
                api_key = ""
                if backend == "OpenCode Zen":
                    api_key = shared.core.opencode_cred.get_key() or ""
                elif backend == "Kilo Gateway":
                    api_key = shared.core.kilo_cred.get_key() or ""
                elif backend == "Custom OpenAI":
                    api_key = shared.core.custom_cred.get_key() or ""
                runner.select_backend(backend)
                message = runner.load(
                    api_key=api_key,
                    model_name=params["model_name"],
                    api_variant=params["api_variant"],
                    kilo_reasoning_enabled=params["kilo_reasoning_enabled"],
                    base_url=params["base_url"],
                )
                return {
                    "status": "success",
                    "message": str(message),
                    "backend": backend,
                }
            except Exception:
                runner.select_backend(previous["backend"])
                runner.openai_runtime = previous["openai_runtime"]
                runner.kilo_reasoning_enabled = previous["kilo_reasoning_enabled"]
                runner.load_error = previous["load_error"]
                raise RuntimeError("runtime_load_failed") from None

    return build_runtime_handlers(
        RuntimeDependencies(
            runner=shared.core.runner,
            generation_scope=generation_scope,
            turn_locks=shared._chat_turn_locks,
            voice_audio=shared.request_voice_audio,
            voice_control=shared.request_voice_control,
            voice_stream=voice_bridge.stream,
            vts_manager=shared._vts_manager,
            runtime_load=runtime_load,
            runtime_request=runtime_request,
            mood_tracker=getattr(shared.core, "mood_tracker", None),
        )
    )


async def _chat_handler(
    dependencies: RuntimeDependencies,
    payload: dict[str, Any],
    cancelled: asyncio.Event,
) -> AsyncIterator[WorkerReply]:
    try:
        request = _parse_chat_request(payload)
    except (TypeError, ValueError):
        yield _error("invalid_request", retryable=False)
        return

    yield WorkerReply(CHAT_RESET, {})
    loop = asyncio.get_running_loop()
    queue: asyncio.Queue[WorkerReply | object] = asyncio.Queue()
    complete = object()
    tool_result_queue = payload.get("__worker_tool_result_queue")
    agent_mode = request.agent_mode and isinstance(tool_result_queue, asyncio.Queue)
    session_id = payload.get("session_id")
    if not isinstance(session_id, str) or not session_id.strip():
        session_id = str(payload.get("__worker_session_id") or "")
    correlation_id = str(payload.get("__worker_correlation_id") or "")

    def publish(value: WorkerReply | object) -> None:
        if value is complete or not cancelled.is_set():
            loop.call_soon_threadsafe(queue.put_nowait, value)

    def on_stream_delta(delta: str) -> None:
        if isinstance(delta, str) and delta:
            publish(WorkerReply(CHAT_CHUNK, {"text": delta}))

    def on_stream_reset() -> None:
        publish(WorkerReply(CHAT_RESET, {}))

    def generate_once(
        prompt: str,
        turns: tuple[ChatTurn, ...],
        *,
        stream: bool,
        observe_user: bool,
    ) -> str:
        with dependencies.turn_locks():
            with dependencies.generation_scope():
                mood_context = None
                if dependencies.mood_tracker is not None:
                    if observe_user:
                        dependencies.mood_tracker.observe_user_input(request.prompt)
                    mood_context = dependencies.mood_tracker.state.describe_for_prompt()
                raw_reply = dependencies.runner.generate_reply(
                    prompt=prompt,
                    max_new_tokens=request.max_tokens,
                    temperature=request.temperature,
                    top_k=request.top_k,
                    allow_web_search=request.allow_web_search and not agent_mode,
                    turns_history=turns,
                    memory_context=request.memory_context,
                    mood_context=mood_context,
                    on_stream_delta=on_stream_delta if stream else None,
                    on_stream_reset=on_stream_reset if stream else None,
                    vts_instruction=request.vts_instruction,
                    is_cancelled=cancelled.is_set,
                    structured_tools_only=agent_mode,
                )
                if dependencies.mood_tracker is not None:
                    _, emotion = extract_reply_emotion(raw_reply)
                    dependencies.mood_tracker.update_from_reply_emotion(emotion)
                return raw_reply

    async def wait_for_tool_result(
        tool_request_id: str,
        deadline_ms: int,
        expected_correlation_id: str,
    ) -> Mapping[str, Any]:
        if not isinstance(tool_result_queue, asyncio.Queue):
            raise RuntimeError("agent_unavailable")
        try:
            result = await asyncio.wait_for(
                tool_result_queue.get(),
                timeout=deadline_ms / 1000.0,
            )
        except asyncio.TimeoutError as error:
            raise RuntimeError("tool_timeout") from error
        if result.get("request_id") != tool_request_id:
            raise RuntimeError("tool_request_mismatch")
        if result.get("session_id") != session_id or result.get("correlation_id") != expected_correlation_id:
            raise RuntimeError("tool_context_mismatch")
        return result

    async def run_generation() -> None:
        try:
            raw_reply = await asyncio.to_thread(
                generate_once,
                request.prompt,
                request.turns,
                stream=not agent_mode,
                observe_user=True,
            )
            if not isinstance(raw_reply, str):
                raise TypeError("model reply must be text")
            if agent_mode:
                try:
                    tool_call = parse_tool_call(raw_reply)
                except ToolCallParseError as error:
                    raise RuntimeError("invalid_tool_call") from error
                if tool_call is not None:
                    tool_request_id = f"tool-{uuid.uuid4().hex}"
                    tool_correlation_id = correlation_id or f"corr-{uuid.uuid4().hex}"
                    deadline_ms = 30_000
                    publish(
                        WorkerReply(
                            CHAT_TOOL_REQUEST,
                            {
                                "request_id": tool_request_id,
                                "session_id": session_id,
                                "correlation_id": tool_correlation_id,
                                "tool_id": tool_call.tool_id,
                                "tool_version": tool_call.tool_version,
                                "arguments": tool_call.arguments,
                                "deadline_ms": deadline_ms,
                            },
                        )
                    )
                    result = await wait_for_tool_result(
                        tool_request_id,
                        deadline_ms,
                        tool_correlation_id,
                    )
                    tool_turn = ChatTurn(
                        "[structured tool result]",
                        json.dumps(
                            {
                                "status": result["status"],
                                "output": result["output"],
                                "safe_summary": result["safe_summary"],
                                "error_code": result.get("error_code"),
                            },
                            ensure_ascii=False,
                            separators=(",", ":"),
                        ),
                    )
                    raw_reply = await asyncio.to_thread(
                        generate_once,
                        request.prompt,
                        request.turns + (tool_turn,),
                        stream=True,
                        observe_user=False,
                    )
            publish(WorkerReply(CHAT_DONE, {"raw_reply": raw_reply}))
        except Exception:
            publish(_error("generation_failed", retryable=True))
        finally:
            publish(complete)

    task = asyncio.create_task(run_generation())
    try:
        while True:
            item = await queue.get()
            if item is complete:
                await task
                return
            if cancelled.is_set():
                return
            if isinstance(item, WorkerReply):
                yield item
    finally:
        if not task.done():
            task.add_done_callback(_consume_task_result)


async def _memory_handler(
    dependencies: RuntimeDependencies,
    payload: dict[str, Any],
    _cancelled: asyncio.Event,
) -> AsyncIterator[WorkerReply]:
    prompt = payload.get("prompt")
    response = payload.get("response")
    if not isinstance(prompt, str) or not isinstance(response, str):
        yield _error("invalid_request", retryable=False)
        return

    def generate_facts(
        fact_prompt: str,
        max_new_tokens: int,
        temperature: float,
        top_k: int,
        allow_web_search: bool,
    ) -> str:
        try:
            with dependencies.generation_scope():
                return dependencies.runner.generate_reply(
                    prompt=fact_prompt,
                    max_new_tokens=max_new_tokens,
                    temperature=temperature,
                    top_k=top_k,
                    allow_web_search=allow_web_search,
                    turns_history=(),
                    memory_context=None,
                    on_stream_delta=None,
                    on_stream_reset=None,
                    vts_instruction="",
                )
        except Exception:
            raise RuntimeError("generation_failed") from None

    facts = await asyncio.to_thread(
        extract_facts_with_llm,
        prompt,
        response,
        generate_facts,
    )
    yield WorkerReply(
        MEMORY_FACTS,
        {
            "facts": [
                {
                    "kind": fact.kind.value,
                    "content": fact.content,
                    "scope": None,
                    "confidence": fact.confidence,
                    "importance": fact.importance,
                }
                for fact in facts[:5]
            ]
        },
    )


async def _runtime_handler(
    dependencies: RuntimeDependencies,
    payload: dict[str, Any],
    _cancelled: asyncio.Event,
) -> AsyncIterator[WorkerReply]:
    if "method" in payload or "path_and_query" in payload:
        async for reply in _runtime_request_handler(dependencies, payload):
            yield reply
        return

    if dependencies.runtime_load is None:
        yield _error("runtime_unavailable", retryable=True)
        return
    sensitive_keys = {
        "api_key",
        "opencode_api_key",
        "kilo_api_key",
        "gemini_vision_api_key",
        "custom_api_key",
        "token",
        "secret",
    }
    if any(key.strip().lower() in sensitive_keys for key in payload):
        yield _error("invalid_request", retryable=False)
        return
    try:
        result = await asyncio.to_thread(dependencies.runtime_load, dict(payload))
        if not isinstance(result, Mapping):
            raise TypeError("runtime result must be an object")
    except Exception:
        yield _error("runtime_load_failed", retryable=False)
        return
    yield WorkerReply(RUNTIME_STATE, dict(result))


async def _runtime_request_handler(
    dependencies: RuntimeDependencies,
    payload: dict[str, Any],
) -> AsyncIterator[WorkerReply]:
    if dependencies.runtime_request is None:
        yield _error("runtime_unavailable", retryable=True)
        return
    try:
        method = _required_text(payload, "method", maximum=16).upper()
        path_and_query = _required_text(payload, "path_and_query", maximum=2_048)
        if method not in {"GET", "POST", "DELETE"} or not path_and_query.startswith("/"):
            raise ValueError("invalid runtime route")
        content_type = payload.get("content_type")
        if content_type is not None and (
            not isinstance(content_type, str) or len(content_type) > 256
        ):
            raise ValueError("invalid content type")
        body = payload.get("body", "")
        if not isinstance(body, str) or len(body.encode("utf-8")) > 8 * 1024 * 1024:
            raise ValueError("invalid body")
    except (TypeError, ValueError):
        yield _error("invalid_request", retryable=False)
        return

    try:
        response = await asyncio.to_thread(
            dependencies.runtime_request,
            method,
            path_and_query,
            content_type,
            body,
        )
        if (
            not isinstance(response, RuntimeHttpResponse)
            or isinstance(response.status_code, bool)
            or response.status_code < 100
            or response.status_code > 599
            or not isinstance(response.content_type, str)
            or not isinstance(response.body, bytes)
        ):
            raise TypeError("invalid runtime response")
        headers = dict(response.headers or {})
        if any(
            not isinstance(key, str)
            or not isinstance(value, str)
            or len(key) > 128
            or len(value) > 1_024
            for key, value in headers.items()
        ):
            raise TypeError("invalid runtime response headers")
    except Exception:
        yield _error("runtime_request_failed", retryable=True)
        return

    response_payload: dict[str, Any] = {
        "status_code": response.status_code,
        "content_type": response.content_type,
        "headers": headers,
    }
    content_type_lower = response.content_type.casefold()
    is_text = content_type_lower.startswith("text/") or (
        "json" in content_type_lower
    )
    if is_text:
        try:
            response_payload["body"] = response.body.decode("utf-8")
        except UnicodeDecodeError:
            yield _error("runtime_request_failed", retryable=True)
            return
        yield WorkerReply(RUNTIME_RESPONSE, response_payload)
        return

    if not response.body:
        response_payload["body"] = ""
        yield WorkerReply(RUNTIME_RESPONSE, response_payload)
        return

    response_payload["binary"] = True
    yield WorkerReply(RUNTIME_RESPONSE, response_payload, binary=response.body)


async def _voice_handler(
    dependencies: RuntimeDependencies,
    payload: dict[str, Any],
    _cancelled: asyncio.Event,
) -> AsyncIterator[WorkerReply]:
    operation = payload.get("operation")
    if operation == "stream":
        if dependencies.voice_stream is None:
            yield _error("voice_unavailable", retryable=True)
            return
        terminal_sent = False
        try:
            async for event in dependencies.voice_stream(dict(payload), _cancelled):
                control = event.control_payload()
                if event.event_type == "audio":
                    if not isinstance(event.audio, bytes) or not event.audio:
                        yield _error("voice_empty_audio", retryable=True)
                        return
                    control["state"] = "audio_ready"
                    yield WorkerReply(VOICE_CONTROL, control, binary=event.audio)
                    continue
                if event.event_type in {"ended", "cancelled", "failed"}:
                    control["state"] = "done"
                    yield WorkerReply(VOICE_DONE, control)
                    terminal_sent = True
                    return
                control["state"] = "control"
                yield WorkerReply(VOICE_CONTROL, control)
        except asyncio.CancelledError:
            raise
        except Exception:
            yield _error("voice_failed", retryable=True)
            return
        if not terminal_sent:
            yield _error("voice_incomplete", retryable=True)
        return

    if operation == "tts":
        if dependencies.voice_audio is None:
            yield _error("voice_unavailable", retryable=True)
            return
        try:
            text = _required_text(payload, "text", maximum=12_000)
            voice_label = _required_text(payload, "voice_label", maximum=128)
            voice_style_id = _required_text(payload, "voice_style_id", maximum=32)
            speed = _bounded_float(payload.get("speed"), 0.5, 2.0)
        except (TypeError, ValueError):
            yield _error("invalid_request", retryable=False)
            return
        try:
            audio = await asyncio.to_thread(
                dependencies.voice_audio,
                text,
                voice_label,
                speed,
                voice_style_id,
            )
        except Exception:
            yield _error("voice_failed", retryable=True)
            return
        if not isinstance(audio, bytes) or not audio:
            yield _error("voice_empty_audio", retryable=True)
            return
        yield WorkerReply(VOICE_CONTROL, {"state": "audio_ready"}, binary=audio)
        yield WorkerReply(VOICE_DONE, {"state": "done"})
        return

    if operation in ("warmup", "release"):
        if dependencies.voice_control is None:
            yield _error("voice_unavailable", retryable=True)
            return
        try:
            voice_label = _required_text(payload, "voice_label", maximum=128)
            result = await asyncio.to_thread(
                dependencies.voice_control,
                f"/{operation}",
                voice_label,
            )
        except Exception:
            yield _error("voice_failed", retryable=True)
            return
        yield WorkerReply(VOICE_DONE, {"state": "done", "result": dict(result)})
        return

    yield _error("unsupported_operation", retryable=False)


async def _vts_handler(
    dependencies: RuntimeDependencies,
    payload: dict[str, Any],
    _cancelled: asyncio.Event,
) -> AsyncIterator[WorkerReply]:
    if dependencies.vts_manager is None:
        yield _error("vts_unavailable", retryable=True)
        return
    try:
        manager = dependencies.vts_manager()
    except Exception:
        yield _error("vts_unavailable", retryable=True)
        return
    if manager is None or not bool(getattr(manager, "is_connected", False)):
        yield _error("vts_unavailable", retryable=True)
        return

    operation = payload.get("operation")
    if operation == "catalog":
        try:
            catalog = await asyncio.to_thread(manager.current_catalog_sync)
        except Exception:
            yield _error("vts_catalog_failed", retryable=True)
            return
        if catalog is None:
            yield _error("vts_catalog_unavailable", retryable=True)
            return
        yield WorkerReply(
            VTS_CATALOG,
            {
                "model_id": catalog.model_id,
                "model_name": catalog.model_name,
                "hotkey_codes": [
                    entry.code for entry in catalog.entries if entry.kind == "hotkey"
                ],
                "expression_codes": [
                    entry.code
                    for entry in catalog.entries
                    if entry.kind == "expression"
                ],
            },
        )
        return

    if operation == "action":
        codes = payload.get("codes")
        if (
            not isinstance(codes, list)
            or not codes
            or any(not isinstance(code, str) or not code for code in codes)
        ):
            yield _error("invalid_request", retryable=False)
            return
        try:
            catalog = await asyncio.to_thread(manager.current_catalog_sync)
            if catalog is None:
                yield _error("vts_catalog_unavailable", retryable=True)
                return
            valid_codes = {entry.code for entry in catalog.entries}
            normalized = tuple(code.strip().lower() for code in codes)
            if len(normalized) != len(set(normalized)) or any(
                code not in valid_codes for code in normalized
            ):
                yield _error("unknown_vts_code", retryable=False)
                return
            await asyncio.to_thread(manager.trigger_catalog_actions_sync, catalog, normalized)
        except Exception:
            yield _error("vts_action_failed", retryable=True)
            return
        yield WorkerReply(VTS_ACTION, {"status": "ok"})
        return

    yield _error("unsupported_operation", retryable=False)


@dataclass(frozen=True, slots=True)
class _ChatRequest:
    prompt: str
    turns: tuple[ChatTurn, ...]
    memory_context: str | None
    vts_instruction: str
    max_tokens: int
    temperature: float
    top_k: int
    allow_web_search: bool
    agent_mode: bool


def _parse_chat_request(payload: Mapping[str, Any]) -> _ChatRequest:
    prompt = _required_text(payload, "prompt", maximum=16_000)
    raw_turns = payload.get("turns")
    if not isinstance(raw_turns, list) or len(raw_turns) > 8:
        raise ValueError("invalid turns")
    turns: list[ChatTurn] = []
    for value in raw_turns:
        if not isinstance(value, Mapping):
            raise ValueError("invalid turn")
        turns.append(
            ChatTurn(
                _required_text(value, "prompt", maximum=16_000),
                _required_text(value, "response", maximum=32_000),
            )
        )

    memory_context = payload.get("memory_context")
    if memory_context is None or memory_context == "":
        memory_context = None
    elif not isinstance(memory_context, str) or len(memory_context) > 100_000:
        raise ValueError("invalid memory context")
    vts_instruction = payload.get("vts_instruction", "")
    if not isinstance(vts_instruction, str) or len(vts_instruction) > 32_000:
        raise ValueError("invalid VTS instruction")
    settings = payload.get("settings")
    if not isinstance(settings, Mapping):
        raise ValueError("invalid settings")

    return _ChatRequest(
        prompt=prompt,
        turns=tuple(turns),
        memory_context=memory_context,
        vts_instruction=vts_instruction,
        max_tokens=_bounded_int(settings.get("max_tokens", 256), 1, 1024),
        temperature=_bounded_float(settings.get("temperature", 0.7), 0.1, 2.0),
        top_k=_bounded_int(settings.get("top_k", 50), 1, 200),
        allow_web_search=_required_bool(
            settings.get("allow_web_search", True)
        ),
        agent_mode=_required_bool(settings.get("agent_mode", False)),
    )


def _required_text(
    payload: Mapping[str, Any],
    key: str,
    *,
    maximum: int,
) -> str:
    value = payload.get(key)
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise ValueError(f"invalid {key}")
    return value


def _bounded_int(value: Any, minimum: int, maximum: int) -> int:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise TypeError("number required")
    parsed = int(value)
    if parsed != value or parsed < minimum or parsed > maximum:
        raise ValueError("number out of range")
    return parsed


def _bounded_float(value: Any, minimum: float, maximum: float) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise TypeError("number required")
    parsed = float(value)
    if not math.isfinite(parsed) or parsed < minimum or parsed > maximum:
        raise ValueError("number out of range")
    return parsed


def _required_bool(value: Any) -> bool:
    if not isinstance(value, bool):
        raise TypeError("boolean required")
    return value


def _error(code: str, *, retryable: bool) -> WorkerReply:
    return WorkerReply(ERROR, {"code": code, "retryable": retryable})


def _consume_task_result(task: asyncio.Task[Any]) -> None:
    with contextlib.suppress(asyncio.CancelledError, Exception):
        task.result()
