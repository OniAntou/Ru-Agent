from __future__ import annotations

import asyncio
import json
import threading
from typing import Annotated

from fastapi import APIRouter, WebSocket, WebSocketDisconnect
from pydantic import BaseModel, Field

from .._server_shared import (
    _TURN_LOCK,
    _MEMORY_TURN_LOCK,
    _VTS_MANAGER,
    _MEMORY_QUEUE,
    _MEMORY_STOP,
    _MEMORY_WORKER_STARTED,
    _SCREEN_REACTIONS,
    _session_append_messages,
    _session_turns,
    _create_session_file,
    _load_session_file,
    _MAX_PROMPT_CHARS,
    _MAX_WS_MESSAGE_BYTES,
    core,
    logger,
    catalog_instruction,
    _safe_put,
    _learn_from_turn,
    _strip_voice_markers,
    effective_settings,
    _DEFAULT_SETTINGS,
    _gpu_lifecycle,
    _valid_desktop_token,
    _trigger_vts_catalog_codes,
)

router = APIRouter()


@router.websocket("/chat")
async def chat_endpoint(websocket: WebSocket):
    if not _valid_desktop_token(websocket.query_params.get("token", "")):
        await websocket.close(code=4401, reason="Unauthorized")
        return
    await websocket.accept()
    logger.info("WebSocket chat endpoint connected")

    loop = asyncio.get_event_loop()
    chat_queue = asyncio.Queue()
    stream_state = {"raw": "", "visible": ""}

    def stream_callback(delta: str):
        from .._text_utils import append_stream_text, safe_streamed_text
        raw = append_stream_text(stream_state["raw"], delta)
        visible = safe_streamed_text(raw)
        previous = stream_state["visible"]
        stream_state["raw"] = raw
        stream_state["visible"] = visible
        if visible.startswith(previous) and len(visible) > len(previous):
            chunk = visible[len(previous):]
            logger.debug("Streaming chunk: %s chars", len(chunk))
            _safe_put(
                loop,
                chat_queue,
                {"type": "chunk", "content": chunk},
            )

    def stream_reset_callback():
        stream_state["raw"] = ""
        stream_state["visible"] = ""
        _safe_put(loop, chat_queue, {"type": "reset"})

    def run_generation(prompt: str, session_id: str):
        full_reply = None
        user_appended = False
        memory_lock_held = False
        try:
            settings = effective_settings()
            if not _MEMORY_TURN_LOCK.acquire(timeout=10.0):
                _safe_put(
                    loop,
                    chat_queue,
                    {
                        "type": "error",
                        "content": "Ru is busy with self-learning; try again in a few seconds.",
                        "session_id": session_id,
                    },
                )
                return
            memory_lock_held = True
            turns_history = _session_turns(session_id)
            if _session_append_messages(session_id, [{"role": "user", "content": prompt}]):
                user_appended = True

            manager = _VTS_MANAGER
            catalog = None
            if manager is not None and manager.is_connected:
                try:
                    catalog = manager.current_catalog_sync()
                except Exception as error:
                    logger.debug("VTS catalog read failed: %s", error)

            if core.runner.backend not in ("opencode", "kilo"):
                with _gpu_lifecycle().local_generation():
                    raw_reply = core.generate_reply_with_memory(
                        prompt=prompt,
                        max_new_tokens=settings.get("max_tokens", _DEFAULT_SETTINGS["max_tokens"]),
                        temperature=settings.get("temperature", _DEFAULT_SETTINGS["temperature"]),
                        top_k=settings.get("top_k", _DEFAULT_SETTINGS["top_k"]),
                        allow_web_search=settings.get("allow_web_search", _DEFAULT_SETTINGS["allow_web_search"]),
                        turns_history=turns_history,
                        on_stream_delta=stream_callback,
                        on_stream_reset=stream_reset_callback,
                        vts_instruction=catalog_instruction(catalog),
                        share_memory_with_cloud=settings.get(
                            "share_memory_with_cloud",
                            _DEFAULT_SETTINGS["share_memory_with_cloud"],
                        ),
                    )
            else:
                raw_reply = core.generate_reply_with_memory(
                    prompt=prompt,
                    max_new_tokens=settings.get("max_tokens", _DEFAULT_SETTINGS["max_tokens"]),
                    temperature=settings.get("temperature", _DEFAULT_SETTINGS["temperature"]),
                    top_k=settings.get("top_k", _DEFAULT_SETTINGS["top_k"]),
                    allow_web_search=settings.get("allow_web_search", _DEFAULT_SETTINGS["allow_web_search"]),
                    turns_history=turns_history,
                    on_stream_delta=stream_callback,
                    on_stream_reset=stream_reset_callback,
                    vts_instruction=catalog_instruction(catalog),
                    share_memory_with_cloud=settings.get(
                        "share_memory_with_cloud",
                        _DEFAULT_SETTINGS["share_memory_with_cloud"],
                    ),
                )
            from .._server_shared import parse_catalog_reply
            full_reply, codes = parse_catalog_reply(raw_reply, catalog)
            full_reply = _strip_voice_markers(full_reply)

            if settings.get("enable_self_learning", _DEFAULT_SETTINGS["enable_self_learning"]):
                core.remember(prompt, full_reply)

            _session_append_messages(session_id, [{"role": "ai", "content": full_reply}])

            if manager is not None and manager.is_connected and catalog is not None and codes:
                try:
                    _trigger_vts_catalog_codes(manager, catalog, codes)
                except Exception as error:
                    logger.debug("VTS marker parse failed: %s", error)

            _safe_put(loop, chat_queue, {"type": "done", "content": full_reply, "session_id": session_id})
        except Exception as error:
            logger.exception("Chat generation failed for prompt %r", prompt[:80])
            if user_appended:
                _session_append_messages(
                    session_id,
                    [{"role": "ai", "content": f"(Lỗi xử lý: {error})"}],
                )
            _safe_put(loop, chat_queue, {"type": "error", "content": str(error), "session_id": session_id})
        finally:
            if memory_lock_held:
                _MEMORY_TURN_LOCK.release()
            _TURN_LOCK.release()
            if full_reply is not None:
                _learn_from_turn(prompt, full_reply)

    try:
        while True:
            data = await websocket.receive_text()
            if len(data) > _MAX_WS_MESSAGE_BYTES:
                await websocket.send_text(
                    json.dumps({"type": "error", "content": "Message too large."})
                )
                continue
            req = json.loads(data)
            prompt = str(req.get("prompt", "")).strip()
            if not prompt:
                continue
            if len(prompt) > _MAX_PROMPT_CHARS:
                await websocket.send_text(
                    json.dumps({"type": "error", "content": f"Prompt too long (max {_MAX_PROMPT_CHARS} characters)."})
                )
                continue
            session_id = str(req.get("session_id", "")).strip()
            if not session_id or _load_session_file(session_id) is None:
                created = _create_session_file()
                if created is None:
                    await websocket.send_text(
                        json.dumps({"type": "error", "content": "Không tạo được session mới."})
                    )
                    continue
                session_id = created
                await websocket.send_text(
                    json.dumps({"type": "session", "session_id": session_id})
                )
            _SCREEN_REACTIONS.cancel_pending()
            if _TURN_LOCK.locked():
                await websocket.send_text(
                    json.dumps({"type": "error", "content": "Ru is already replying; wait for the current reply to finish.", "session_id": session_id})
                )
                continue
            _TURN_LOCK.acquire()
            try:
                threading.Thread(target=run_generation, args=(prompt, session_id), daemon=True).start()
            except Exception:
                _TURN_LOCK.release()
                raise
            while True:
                message = await chat_queue.get()
                await websocket.send_text(json.dumps(message))
                if message["type"] in ("done", "error"):
                    break
    except WebSocketDisconnect:
        logger.info("Client disconnected")
    except Exception as error:
        logger.error("WebSocket error: %s", error)
