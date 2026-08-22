from __future__ import annotations

import asyncio
import json
from collections.abc import AsyncIterator
from types import SimpleNamespace

import pytest

import ru_runtime.memory.store
import ru_runtime.worker.v1.handlers as handlers_module
from ru_runtime.vts.catalog import VtsCatalog, VtsCatalogEntry
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
from ru_runtime.worker.v1.handlers import (
    RuntimeDependencies,
    RuntimeHttpResponse,
    build_runtime_handlers,
)
from ru_runtime.worker.v1.server import WorkerReply


def test_chat_uses_only_explicit_prompt_history_memory_and_settings() -> None:
    runner = FakeRunner("Em nghe đây. [[VTS:vts_01]] [[VOICE_PACE:normal]]")
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))
    payload = {
        "prompt": "Anh đang làm gì?",
        "session_id": "session-1",
        "turns": [{"prompt": "Chào em", "response": "Em chào anh."}],
        "memory_context": "Anh đang làm dự án D:/LLM.",
        "vts_instruction": "private catalog instruction",
        "settings": {
            "max_tokens": 128,
            "temperature": 0.4,
            "top_k": 32,
            "allow_web_search": False,
            "model_tools": [],
        },
    }

    replies = asyncio.run(collect(handlers.chat(payload, asyncio.Event())))

    call = runner.calls[0]
    assert call["prompt"] == payload["prompt"]
    assert call["memory_context"] == payload["memory_context"]
    assert call["vts_instruction"] == payload["vts_instruction"]
    assert call["max_new_tokens"] == 128
    assert call["temperature"] == 0.4
    assert call["top_k"] == 32
    assert call["allow_web_search"] is False
    assert [(turn.prompt, turn.response) for turn in call["turns_history"]] == [
        ("Chào em", "Em chào anh.")
    ]
    assert [reply.type for reply in replies] == [CHAT_RESET, CHAT_CHUNK, CHAT_DONE]
    assert replies[1].payload == {"text": "Em nghe đây. "}
    assert replies[2].payload == {
        "raw_reply": "Em nghe đây. [[VTS:vts_01]] [[VOICE_PACE:normal]]"
    }
    assert "content" not in replies[2].payload


def test_chat_never_opens_the_python_memory_store(monkeypatch) -> None:
    def fail_if_opened(*_args, **_kwargs):
        raise AssertionError("Python memory store must not be opened by the worker")

    monkeypatch.setattr(
        ru_runtime.memory.store.StructuredMemoryStore,
        "__init__",
        fail_if_opened,
    )
    handlers = build_runtime_handlers(
        RuntimeDependencies(runner=FakeRunner("Em nghe đây."))
    )

    replies = asyncio.run(
        collect(
            handlers.chat(
                {
                    "prompt": "prompt",
                    "session_id": "session",
                    "turns": [],
                    "memory_context": "explicit ASP.NET memory",
                    "vts_instruction": "",
                    "settings": {"model_tools": []},
                },
                asyncio.Event(),
            )
        )
    )

    assert replies[-1].type == CHAT_DONE


def test_memory_extraction_returns_at_most_five_typed_facts() -> None:
    raw_facts = [
        {
            "kind": "preference",
            "content": f"preference {index}",
            "confidence": 0.9,
            "importance": 0.7,
        }
        for index in range(8)
    ]
    runner = FakeRunner(json.dumps(raw_facts))
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))

    replies = asyncio.run(
        collect(
            handlers.memory_extract(
                {"prompt": "prompt", "response": "response"},
                asyncio.Event(),
            )
        )
    )

    reply = single(replies)
    assert reply.type == MEMORY_FACTS
    assert len(reply.payload["facts"]) == 5
    assert all(fact["kind"] == "preference" for fact in reply.payload["facts"])
    call = runner.calls[0]
    assert call["memory_context"] is None
    assert call["turns_history"] == ()
    assert call["vts_instruction"] == ""


def test_voice_tts_pairs_control_with_non_empty_binary() -> None:
    observed: list[tuple[str, str, float, str]] = []

    def voice_audio(text: str, label: str, speed: float, style: str) -> bytes:
        observed.append((text, label, speed, style))
        return b"RIFF-wave"

    handlers = build_runtime_handlers(
        RuntimeDependencies(runner=FakeRunner("[]"), voice_audio=voice_audio)
    )
    replies = asyncio.run(
        collect(
            handlers.voice(
                {
                    "operation": "tts",
                    "text": "Em nghe đây.",
                    "voice_label": "Ru (OmniVoice)",
                    "speed": 1.0,
                    "voice_style_id": "original",
                },
                asyncio.Event(),
            )
        )
    )

    assert observed == [("Em nghe đây.", "Ru (OmniVoice)", 1.0, "original")]
    assert [reply.type for reply in replies] == [VOICE_CONTROL, VOICE_DONE]
    assert replies[0].payload == {"state": "audio_ready"}
    assert replies[0].binary == b"RIFF-wave"
    assert replies[1].payload == {"state": "done"}


def test_voice_stream_maps_public_controls_and_binary_in_order() -> None:
    async def voice_stream(payload: dict, _cancelled: asyncio.Event):
        assert payload["request_id"] == "voice-stream-1"
        yield VoiceStreamEvent("started", "voice-stream-1", 0)
        yield VoiceStreamEvent(
            "audio",
            "voice-stream-1",
            0,
            audio=b"RIFF-wave",
            sample_rate=24_000,
            audio_format="wav",
        )
        yield VoiceStreamEvent("ended", "voice-stream-1", 1)

    handlers = build_runtime_handlers(
        RuntimeDependencies(
            runner=FakeRunner("[]"),
            voice_stream=voice_stream,
        )
    )

    replies = asyncio.run(
        collect(
            handlers.voice(
                {
                    "operation": "stream",
                    "request_id": "voice-stream-1",
                    "text": "synthetic",
                    "voice_label": "Ru (OmniVoice)",
                    "speed": 1.0,
                    "voice_style_id": "original",
                },
                asyncio.Event(),
            )
        )
    )

    assert [reply.type for reply in replies] == [
        VOICE_CONTROL,
        VOICE_CONTROL,
        VOICE_DONE,
    ]
    assert replies[0].payload == {
        "state": "control",
        "type": "started",
        "request_id": "voice-stream-1",
        "sequence": 0,
    }
    assert replies[1].payload == {
        "state": "audio_ready",
        "type": "audio",
        "request_id": "voice-stream-1",
        "sequence": 0,
        "sample_rate": 24_000,
        "format": "wav",
    }
    assert replies[1].binary == b"RIFF-wave"
    assert replies[2].payload["type"] == "ended"


def test_vts_catalog_and_actions_stay_on_the_private_handler() -> None:
    manager = FakeVtsManager()
    handlers = build_runtime_handlers(
        RuntimeDependencies(
            runner=FakeRunner("[]"),
            vts_manager=lambda: manager,
        )
    )

    catalog_reply = single(
        asyncio.run(
            collect(handlers.vts({"operation": "catalog"}, asyncio.Event()))
        )
    )
    action_reply = single(
        asyncio.run(
            collect(
                handlers.vts(
                    {"operation": "action", "codes": ["vts_01"]},
                    asyncio.Event(),
                )
            )
        )
    )

    assert catalog_reply.type == VTS_CATALOG
    assert catalog_reply.payload["hotkey_codes"] == ["vts_01"]
    assert action_reply.type == VTS_ACTION
    assert action_reply.payload == {"status": "ok"}
    assert manager.triggered == [("vts_01",)]


def test_runtime_failures_emit_only_stable_codes(caplog) -> None:
    runner = FakeRunner(RuntimeError("secret token and private model path"))
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))

    replies = asyncio.run(
        collect(
            handlers.chat(
                {
                    "prompt": "prompt",
                    "session_id": "session",
                    "turns": [],
                    "memory_context": "",
                    "vts_instruction": "",
                    "settings": {"model_tools": []},
                },
                asyncio.Event(),
            )
        )
    )

    assert replies[-1].type == ERROR
    assert replies[-1].payload == {
        "code": "generation_failed",
        "retryable": True,
    }
    assert "secret token" not in caplog.text
    assert "private model path" not in caplog.text


def test_runtime_load_delegates_without_persisting_settings_or_credentials() -> None:
    observed = []

    def runtime_load(payload: dict) -> dict:
        observed.append(payload)
        return {
            "status": "success",
            "message": "Runtime loaded.",
            "backend": payload["backend"],
        }

    handlers = build_runtime_handlers(
        RuntimeDependencies(
            runner=FakeRunner("[]"),
            runtime_load=runtime_load,
        )
    )
    payload = {
        "backend": "OpenCode Zen",
        "model": "opencode/free",
        "opencode_model": "opencode/free",
        "kilo_model": "kilo-auto/free",
        "api_variant": "Default",
        "kilo_reasoning_enabled": False,
    }

    reply = single(
        asyncio.run(collect(handlers.runtime(payload, asyncio.Event())))
    )

    assert observed == [payload]
    assert reply.type == RUNTIME_STATE
    assert reply.payload["status"] == "success"


def test_runtime_load_failure_is_redacted(caplog) -> None:
    def runtime_load(_payload: dict) -> dict:
        raise RuntimeError("private credential and model path")

    handlers = build_runtime_handlers(
        RuntimeDependencies(
            runner=FakeRunner("[]"),
            runtime_load=runtime_load,
        )
    )

    reply = single(
        asyncio.run(
            collect(
                handlers.runtime(
                    {"backend": "OpenCode Zen"},
                    asyncio.Event(),
                )
            )
        )
    )

    assert reply.type == ERROR
    assert reply.payload == {"code": "runtime_load_failed", "retryable": False}
    assert "private credential" not in caplog.text


def test_runtime_load_rollback_restores_load_error(monkeypatch) -> None:
    from ru_runtime.worker.v1 import handlers as handlers_module

    class FakeRunner:
        def __init__(self) -> None:
            self.backend = "OpenCode Zen"
            self.openai_runtime = None
            self.kilo_reasoning_enabled = False
            self.load_error = "previous load error"

        def select_backend(self, backend: str) -> None:
            self.backend = backend

        def load(self, **_kwargs) -> str:
            raise RuntimeError("boom")

    runner = FakeRunner()
    monkeypatch.setattr(
        "ru_runtime.worker.v1.runtime_services.core",
        SimpleNamespace(
            runner=runner,
            opencode_cred=SimpleNamespace(get_key=lambda: ""),
            kilo_cred=SimpleNamespace(get_key=lambda: ""),
            custom_cred=SimpleNamespace(get_key=lambda: ""),
        ),
    )

    handlers = handlers_module.create_runtime_handlers()
    reply = single(
        asyncio.run(
            collect(
                handlers.runtime(
                    {"backend": "Ollama", "ollama_model": "qwen3:8b"},
                    asyncio.Event(),
                )
            )
        )
    )

    assert reply.type == ERROR
    assert reply.payload == {"code": "runtime_load_failed", "retryable": False}
    assert runner.backend == "OpenCode Zen"
    assert runner.load_error == "previous load error"


def test_runtime_load_rejects_custom_api_key_in_the_payload() -> None:
    called = []

    def runtime_load(payload: dict) -> dict:
        called.append(payload)
        return {"status": "success"}

    handlers = build_runtime_handlers(
        RuntimeDependencies(
            runner=FakeRunner("[]"),
            runtime_load=runtime_load,
        )
    )

    reply = single(
        asyncio.run(
            collect(
                handlers.runtime(
                    {"backend": "Custom OpenAI", "custom_api_key": "sk-x"},
                    asyncio.Event(),
                )
            )
        )
    )

    assert reply.type == ERROR
    assert reply.payload == {"code": "invalid_request", "retryable": False}
    assert called == []


def test_runtime_request_delegates_exact_route_and_returns_json() -> None:
    observed = []

    def runtime_request(method: str, path: str, content_type: str | None, body: str):
        observed.append((method, path, content_type, body))
        return RuntimeHttpResponse(
            200,
            "application/json",
            b'{"status":"ok"}',
            {"x-runtime": "worker"},
        )

    handlers = build_runtime_handlers(
        RuntimeDependencies(
            runner=FakeRunner("[]"),
            runtime_request=runtime_request,
        )
    )
    payload = {
        "method": "POST",
        "path_and_query": "/observation/cancel",
        "content_type": "application/json",
        "body": '{"observation_id":"obs-1"}',
    }

    reply = single(asyncio.run(collect(handlers.runtime(payload, asyncio.Event()))))

    assert observed == [
        (
            "POST",
            "/observation/cancel",
            "application/json",
            '{"observation_id":"obs-1"}',
        )
    ]
    assert reply.type == RUNTIME_RESPONSE
    assert reply.payload == {
        "status_code": 200,
        "content_type": "application/json",
        "headers": {"x-runtime": "worker"},
        "body": '{"status":"ok"}',
    }
    assert reply.binary is None


def test_runtime_request_returns_binary_without_base64_expansion() -> None:
    handlers = build_runtime_handlers(
        RuntimeDependencies(
            runner=FakeRunner("[]"),
            runtime_request=lambda *_args: RuntimeHttpResponse(
                200,
                "audio/wav",
                b"RIFF-wave",
            ),
        )
    )

    reply = single(
        asyncio.run(
            collect(
                handlers.runtime(
                    {
                        "method": "POST",
                        "path_and_query": "/voice/tts",
                        "content_type": "application/json",
                        "body": "{}",
                    },
                    asyncio.Event(),
                )
            )
        )
    )

    assert reply.type == RUNTIME_RESPONSE
    assert reply.payload == {
        "status_code": 200,
        "content_type": "audio/wav",
        "headers": {},
        "binary": True,
    }
    assert reply.binary == b"RIFF-wave"


def test_runtime_load_params_maps_ollama_lmstudio_and_custom() -> None:
    from ru_runtime.worker.v1.handlers import _runtime_load_params

    ollama = _runtime_load_params(
        {
            "backend": "Ollama",
            "model": "LFM2.5-2.6B-Q4_K_M.gguf",
            "ollama_model": "qwen3:8b",
            "ollama_url": "http://127.0.0.1:11434/v1",
        }
    )
    assert ollama["backend"] == "Ollama"
    assert ollama["model_name"] == "qwen3:8b"
    assert ollama["base_url"] == "http://127.0.0.1:11434/v1"

    lmstudio = _runtime_load_params(
        {
            "backend": "LM Studio",
            "model": "LFM2.5-2.6B-Q4_K_M.gguf",
            "lmstudio_model": "local-model",
            "lmstudio_url": "http://127.0.0.1:1234/v1",
        }
    )
    assert lmstudio["model_name"] == "local-model"
    assert lmstudio["base_url"] == "http://127.0.0.1:1234/v1"

    custom = _runtime_load_params(
        {
            "backend": "Custom OpenAI",
            "model": "LFM2.5-2.6B-Q4_K_M.gguf",
            "custom_model": "my-model",
            "custom_url": "http://localhost:9000/v1",
        }
    )
    assert custom["model_name"] == "my-model"
    assert custom["base_url"] == "http://localhost:9000/v1"

    kilo = _runtime_load_params(
        {"backend": "Kilo Gateway", "kilo_model": "kilo-auto/free"}
    )
    assert kilo["model_name"] == "kilo-auto/free"
    assert kilo["base_url"] is None

    migrated = _runtime_load_params(
        {"backend": "OpenCode Zen", "model": "LFM2.5-2.6B-Q4_K_M.gguf"}
    )
    assert migrated["model_name"] == ""


def test_runtime_load_params_rejects_retired_and_unknown_backends() -> None:
    from ru_runtime.worker.v1.handlers import _runtime_load_params

    for backend in ("llama.cpp", "gguf", "unknown"):
        with pytest.raises(ValueError, match="unsupported runtime backend"):
            _runtime_load_params({"backend": backend})


def test_runtime_load_params_rejects_non_boolean_reasoning() -> None:
    from ru_runtime.worker.v1.handlers import _runtime_load_params

    with pytest.raises(ValueError, match="invalid reasoning setting"):
        _runtime_load_params(
            {"backend": "Ollama", "kilo_reasoning_enabled": "yes"}
        )


class FakeRunner:
    def __init__(self, result: str | Exception) -> None:
        self.result = result
        self.calls: list[dict] = []
        self.backend = "llama.cpp"

    def generate_reply(self, **kwargs) -> str:
        self.calls.append(kwargs)
        if isinstance(self.result, Exception):
            raise self.result
        callback = kwargs.get("on_stream_delta")
        if callback is not None and not self.result.lstrip().startswith("["):
            callback("Em nghe đây. ")
        return self.result


class SequencedRunner:
    def __init__(self, *results: str) -> None:
        self.results = list(results)
        self.calls: list[dict] = []
        self.backend = "llama.cpp"

    def generate_reply(self, **kwargs) -> str:
        self.calls.append(kwargs)
        result = self.results.pop(0)
        callback = kwargs.get("on_stream_delta")
        if callback is not None:
            callback(result)
        return result


def _model_tools() -> list[dict[str, object]]:
    return [
        {
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "scopes": ["system.write"],
            "availability": "native_pending",
            "model_enabled": True,
            "capability_class": "native_mutation",
        }
    ]


def _action_payload(
    tool_result_queue: asyncio.Queue,
    agent_mode: bool,
    *,
    prompt: str = "Hạ âm lượng còn 2%",
    model_tools: list[dict[str, object]] | None = None,
) -> dict:
    return {
        "prompt": prompt,
        "session_id": "session-1",
        "turns": [],
        "memory_context": None,
        "vts_instruction": "",
        "settings": {
            "agent_mode": agent_mode,
            "allow_web_search": False,
            "model_tools": _model_tools() if model_tools is None else model_tools,
        },
        "__worker_tool_result_queue": tool_result_queue,
        "__worker_correlation_id": "correlation-1",
    }


def _verified_result() -> dict[str, object]:
    return {
        "status": "succeeded",
        "output": {"operation": "set", "level": 2, "muted": False},
        "safe_summary": "Đã xác minh âm lượng ở mức 2%.",
        "error_code": None,
        "duration_ms": 12,
        "verification_state": "verified",
        "endpoint_role": "default_render_multimedia",
        "endpoint_hash": "0123456789abcdef",
    }


async def _run_action_turn(
    runner: SequencedRunner,
    result: dict[str, object],
    agent_mode: bool,
) -> list[WorkerReply]:
    tool_result_queue: asyncio.Queue = asyncio.Queue()
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))
    replies = []
    stream = handlers.chat(
        _action_payload(tool_result_queue, agent_mode),
        asyncio.Event(),
    )
    replies.append(await anext(stream))
    request = await anext(stream)
    assert request.type == CHAT_TOOL_REQUEST
    replies.append(request)
    await tool_result_queue.put(
        {
            "request_id": request.payload["request_id"],
            "session_id": request.payload["session_id"],
            "correlation_id": request.payload["correlation_id"],
            **result,
        }
    )
    replies.extend([reply async for reply in stream])
    return replies


@pytest.mark.parametrize("agent_mode", (False, True))
def test_volume_request_uses_selection_then_verified_receipt(agent_mode: bool) -> None:
    runner = SequencedRunner(
        '{"tool_id":"system.volume","tool_version":"1.0.0","arguments":{"operation":"set","level":2}}',
        "Đã đặt âm lượng ở mức 2% cho anh.",
    )

    replies = asyncio.run(
        _run_action_turn(
            runner,
            _verified_result(),
            agent_mode,
        )
    )

    assert [call["structured_tools_only"] for call in runner.calls] == [True, False]
    assert [call["agent_follow_up"] for call in runner.calls] == [False, True]
    assert runner.calls[0]["action_capabilities"]
    assert runner.calls[1]["action_capabilities"] == ()
    assert replies[-1].type == CHAT_DONE
    assert replies[-1].payload["raw_reply"] == "Đã đặt âm lượng ở mức 2% cho anh."


@pytest.mark.parametrize("agent_mode", (False, True))
def test_volume_model_prose_cannot_claim_success_without_a_request(agent_mode: bool) -> None:
    queue: asyncio.Queue = asyncio.Queue()
    runner = SequencedRunner("Đã chỉnh xong")
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))

    replies = asyncio.run(collect(handlers.chat(
        _action_payload(queue, agent_mode),
        asyncio.Event(),
    )))

    assert all(reply.type != CHAT_TOOL_REQUEST for reply in replies)
    assert replies[-1].type == CHAT_DONE
    assert replies[-1].payload["raw_reply"] == "Không thể thực hiện thay đổi âm lượng."
    assert len(runner.calls) == 1


def test_ordinary_normal_chat_streams_without_entering_selection() -> None:
    queue: asyncio.Queue = asyncio.Queue()
    runner = SequencedRunner("Em nghe đây.")
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))

    replies = asyncio.run(collect(handlers.chat(
        _action_payload(queue, False, prompt="Kể em nghe chuyện hôm nay", model_tools=[]),
        asyncio.Event(),
    )))

    assert len(runner.calls) == 1
    assert runner.calls[0]["structured_tools_only"] is False
    assert [reply.type for reply in replies] == [CHAT_RESET, CHAT_CHUNK, CHAT_DONE]


def test_ordinary_agent_chat_may_finish_with_plain_selection_prose() -> None:
    queue: asyncio.Queue = asyncio.Queue()
    runner = SequencedRunner("Em đang nghe anh đây.")
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))

    replies = asyncio.run(collect(handlers.chat(
        _action_payload(queue, True, prompt="Hôm nay em thế nào?"),
        asyncio.Event(),
    )))

    assert len(runner.calls) == 1
    assert runner.calls[0]["structured_tools_only"] is True
    assert all(reply.type != CHAT_TOOL_REQUEST for reply in replies)
    assert replies[-1].payload["raw_reply"] == "Em đang nghe anh đây."


@pytest.mark.parametrize("verification_state", ("pending", "failed", "not_required"))
def test_unverified_success_never_runs_follow_up(verification_state: str) -> None:
    runner = SequencedRunner(
        '{"tool_id":"system.volume","tool_version":"1.0.0","arguments":{"operation":"set","level":2}}',
        "Đã hạ âm lượng xuống 2% rồi anh.",
    )
    result = _verified_result()
    result["verification_state"] = verification_state

    replies = asyncio.run(_run_action_turn(runner, result, True))

    assert len(runner.calls) == 1
    assert replies[-1].type == CHAT_DONE
    assert replies[-1].payload["raw_reply"] == "Không thể thực hiện thay đổi âm lượng."
    assert "Đã hạ âm lượng" not in replies[-1].payload["raw_reply"]


def test_failed_receipt_is_truthful_without_success_continuation() -> None:
    runner = SequencedRunner(
        '{"tool_id":"system.volume","tool_version":"1.0.0","arguments":{"operation":"set","level":2}}',
        "Đã hạ âm lượng xuống 2% rồi anh.",
    )
    result = _verified_result()
    result.update(
        status="failed",
        error_code="readback_mismatch",
        safe_summary="Volume change could not be verified.",
        verification_state="failed",
    )

    replies = asyncio.run(_run_action_turn(runner, result, True))

    assert len(runner.calls) == 1
    assert replies[-1].type == CHAT_DONE
    assert replies[-1].payload["raw_reply"] == "Không thể thực hiện thay đổi âm lượng."


def test_mismatched_receipt_is_an_action_failure_not_generation_failure() -> None:
    runner = SequencedRunner(
        '{"tool_id":"system.volume","tool_version":"1.0.0","arguments":{"operation":"mute"}}'
    )
    result = _verified_result()
    result["request_id"] = "stale-request"

    replies = asyncio.run(_run_action_turn(runner, result, True))

    assert all(reply.type != ERROR for reply in replies)
    assert replies[-1].type == CHAT_DONE
    assert replies[-1].payload["raw_reply"] == "Không thể thực hiện thay đổi âm lượng."


def test_late_receipt_timeout_is_an_action_failure(monkeypatch) -> None:
    monkeypatch.setattr(handlers_module, "_ACTION_DEADLINE_MS", 1)

    async def run() -> list[WorkerReply]:
        queue: asyncio.Queue = asyncio.Queue()
        runner = SequencedRunner(
            '{"tool_id":"system.volume","tool_version":"1.0.0","arguments":{"operation":"mute"}}'
        )
        handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))
        return await collect(handlers.chat(_action_payload(queue, True), asyncio.Event()))

    replies = asyncio.run(run())

    assert all(reply.type != ERROR for reply in replies)
    assert replies[-1].payload["raw_reply"] == "Không thể thực hiện thay đổi âm lượng."


def test_two_tool_calls_are_rejected_without_emitting_a_request() -> None:
    queue: asyncio.Queue = asyncio.Queue()
    runner = SequencedRunner(
        '[{"tool_id":"system.volume","tool_version":"1.0.0","arguments":{"operation":"mute"}},'
        '{"tool_id":"system.volume","tool_version":"1.0.0","arguments":{"operation":"unmute"}}]'
    )
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))

    replies = asyncio.run(collect(handlers.chat(
        _action_payload(queue, True),
        asyncio.Event(),
    )))

    assert all(reply.type != CHAT_TOOL_REQUEST for reply in replies)
    assert replies[-1].payload["raw_reply"] == "Không thể thực hiện thay đổi âm lượng."


def test_legacy_volume_marker_is_translated_then_waits_for_verified_receipt() -> None:
    runner = SequencedRunner(
        "[VOLUME: set 2] Em đã chỉnh xong.",
        "Đã đặt âm lượng ở mức 2% cho anh.",
    )

    replies = asyncio.run(_run_action_turn(runner, _verified_result(), False))

    request = next(reply for reply in replies if reply.type == CHAT_TOOL_REQUEST)
    assert request.payload["tool_id"] == "system.volume"
    assert request.payload["arguments"] == {"operation": "set", "level": 2}
    assert len(runner.calls) == 2
    assert replies[-1].payload["raw_reply"] == "Đã đặt âm lượng ở mức 2% cho anh."


def test_capability_absent_volume_request_fails_without_model_generation() -> None:
    queue: asyncio.Queue = asyncio.Queue()
    runner = SequencedRunner("Đã chỉnh xong")
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))

    replies = asyncio.run(collect(handlers.chat(
        _action_payload(queue, False, model_tools=[]),
        asyncio.Event(),
    )))

    assert runner.calls == []
    assert all(reply.type != CHAT_TOOL_REQUEST for reply in replies)
    assert replies[-1].payload["raw_reply"] == "Không thể thực hiện thay đổi âm lượng."


class FakeVtsManager:
    def __init__(self) -> None:
        self.is_connected = True
        self.triggered: list[tuple[str, ...]] = []
        self.catalog = VtsCatalog(
            model_id="model-1",
            model_name="Ru",
            entries=(
                VtsCatalogEntry("vts_01", "hotkey", "Smile", "hotkey-1"),
                VtsCatalogEntry("expr_01", "expression", "Blush", "blush.exp3.json"),
            ),
        )

    def current_catalog_sync(self) -> VtsCatalog:
        return self.catalog

    def trigger_catalog_actions_sync(
        self,
        catalog: VtsCatalog,
        codes: tuple[str, ...],
    ) -> None:
        assert catalog is self.catalog
        self.triggered.append(codes)


async def collect(replies: AsyncIterator[WorkerReply]) -> list[WorkerReply]:
    return [reply async for reply in replies]


def single(replies: list[WorkerReply]) -> WorkerReply:
    assert len(replies) == 1
    return replies[0]
