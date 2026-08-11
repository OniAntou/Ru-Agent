from __future__ import annotations

import asyncio
import json
from collections.abc import AsyncIterator

import mini_gpt.memory
from mini_gpt.vts_catalog import VtsCatalog, VtsCatalogEntry
from mini_gpt.worker_protocol import (
    CHAT_CHUNK,
    CHAT_DONE,
    CHAT_RESET,
    ERROR,
    MEMORY_FACTS,
    RUNTIME_STATE,
    VOICE_CONTROL,
    VOICE_DONE,
    VTS_ACTION,
    VTS_CATALOG,
)
from mini_gpt.worker_runtime_handlers import RuntimeDependencies, build_runtime_handlers
from mini_gpt.worker_server import WorkerReply


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
        mini_gpt.memory.StructuredMemoryStore,
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
                    "settings": {},
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
                    "settings": {},
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
        "checkpoint_path": "",
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
