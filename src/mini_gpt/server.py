import asyncio
import base64
import binascii
import json
import logging
import os
import queue
import re
import socket
import subprocess
import threading
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
from uuid import uuid4

import uvicorn
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import Response
from pydantic import BaseModel

from mini_gpt._atomic_write import atomic_write_json
from mini_gpt.backend_core import MiniGPTCore
from mini_gpt.chat import ChatTurn
from mini_gpt.headless_runner import (
    BACKEND_GGUF,
    BACKEND_KILO,
    BACKEND_OPENCODE,
    BACKEND_TRANSFORMERS,
)
from mini_gpt.memory import MemoryKind
from mini_gpt.memory_extraction import is_sensitive_or_unsafe
from mini_gpt.memory_learning import ExtractFact, extract_facts_with_llm
from mini_gpt.speech_runtime import (
    CapturedAudio,
    SpeechEngineConfig,
    SpeechRecognitionError,
    build_speech_recognizer,
)
from mini_gpt.vts_actions import VtsActions
from mini_gpt.vts_catalog import parse_catalog_reply
from mini_gpt.vts_manager import VTSManager
from mini_gpt.voice_runtime import (
    VOICE_SELECTIONS,
    voice_selection_by_label,
    voice_selection_from_saved,
    voice_service_config,
    validate_voice_environment,
)

logger = logging.getLogger(__name__)

app = FastAPI(title="MiniGPT Headless Backend")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

SETTINGS_FILE = Path.home() / ".minigpt" / "settings.json"
HISTORY_FILE = Path.home() / ".minigpt" / "history.json"
SESSION_DIR = Path.home() / ".minigpt" / "sessions"
PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent

_MAX_HISTORY_MESSAGES = 200
_MAX_CONTEXT_TURNS = 8
_MAX_SESSION_TITLE_CHARS = 50
_DEFAULT_SESSION_TITLE = "Cuộc trò chuyện mới"
_SESSION_ID_RE = re.compile(r"^[a-zA-Z0-9_-]{1,64}$")

_HISTORY_LOCK = threading.Lock()
_TURN_LOCK = threading.Lock()
_VOICE_LOCK = threading.Lock()
_VTS_LOCK = threading.Lock()
_MEMORY_QUEUE_MAXSIZE = 50
_MEMORY_TURN_LOCK = threading.Lock()
_MEMORY_QUEUE: queue.Queue = queue.Queue(maxsize=_MEMORY_QUEUE_MAXSIZE)
_MEMORY_WORKER_STARTED = threading.Event()
_SESSION_TURNS: list[ChatTurn] = []

_VTS_MANAGER: VTSManager | None = None
_VTS_THREAD: threading.Thread | None = None

_VALID_BACKENDS = (BACKEND_TRANSFORMERS, BACKEND_GGUF, BACKEND_OPENCODE, BACKEND_KILO)

VOICE_START_TIMEOUT_SECONDS = 45.0
_VOICE_PROBE_TIMEOUT_SECONDS = 0.25
_VOICE_TTS_TIMEOUT_SECONDS = 90.0
_VOICE_LIFECYCLE_TIMEOUT_SECONDS = 30.0
VTS_CONNECT_TIMEOUT_SECONDS = 20.0
_MAX_ERROR_BODY_BYTES = 4096

_DEFAULT_SETTINGS = {
    "backend": BACKEND_GGUF,
    "model": "deepseek-v4-flash-free",
    "api_variant": "Default",
    "max_tokens": 1024,
    "temperature": 0.7,
    "top_k": 50,
    "allow_web_search": True,
    "enable_self_learning": True,
    "checkpoint_path": "",
    "voice_label": "Ru (OmniVoice)",
    "voice_speed": 1.0,
}


def load_settings() -> dict:
    if SETTINGS_FILE.exists():
        try:
            data = json.loads(SETTINGS_FILE.read_text(encoding="utf-8"))
            if isinstance(data, dict):
                return data
        except (OSError, json.JSONDecodeError) as error:
            logger.warning("Could not read settings file %s: %s", SETTINGS_FILE, error)
    return {}


def save_settings_to_file(data: dict) -> None:
    atomic_write_json(SETTINGS_FILE, data, indent=2)


def load_history() -> list[dict[str, str]]:
    with _HISTORY_LOCK:
        if not HISTORY_FILE.is_file():
            return []
        try:
            data = json.loads(HISTORY_FILE.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            logger.warning("Could not read history file %s: %s", HISTORY_FILE, error)
            return []
        if not isinstance(data, list):
            return []
        return [
            item
            for item in data
            if isinstance(item, dict) and item.get("role") in ("user", "ai") and isinstance(item.get("content"), str)
        ]


def save_history(messages: list[dict[str, str]]) -> None:
    with _HISTORY_LOCK:
        atomic_write_json(HISTORY_FILE, messages[-_MAX_HISTORY_MESSAGES:], indent=2)


def _session_id_is_valid(session_id: str) -> bool:
    return bool(_SESSION_ID_RE.fullmatch(session_id))


def _session_path(session_id: str) -> Path:
    return SESSION_DIR / f"{session_id}.json"


def _utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def _session_title_from_messages(messages: list[dict]) -> str:
    for message in messages:
        if message.get("role") == "user":
            text = " ".join(str(message.get("content", "")).split())
            if text:
                return text[:_MAX_SESSION_TITLE_CHARS]
    return _DEFAULT_SESSION_TITLE


def _valid_session_messages(messages: object) -> list[dict]:
    if not isinstance(messages, list):
        return []
    return [
        message
        for message in messages
        if isinstance(message, dict)
        and message.get("role") in ("user", "ai")
        and isinstance(message.get("content"), str)
    ]


def _load_session_file(session_id: str) -> dict | None:
    if not _session_id_is_valid(session_id):
        return None
    path = _session_path(session_id)
    if not path.is_file():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        logger.warning("Could not read session %s: %s", session_id, error)
        return None
    if not isinstance(data, dict) or "messages" not in data:
        return None
    data["messages"] = _valid_session_messages(data.get("messages"))
    return data


def _save_session_file(session_id: str, data: dict) -> bool:
    try:
        SESSION_DIR.mkdir(parents=True, exist_ok=True)
        atomic_write_json(_session_path(session_id), data, indent=2)
        return True
    except OSError as error:
        logger.error("Could not save session %s: %s", session_id, error)
        return False


def _new_session_id() -> str:
    return uuid4().hex


def _create_session_file() -> str | None:
    session_id = _new_session_id()
    now = _utc_now_iso()
    data = {
        "id": session_id,
        "title": _DEFAULT_SESSION_TITLE,
        "created_at": now,
        "updated_at": now,
        "messages": [],
    }
    if not _save_session_file(session_id, data):
        return None
    return session_id


def load_sessions() -> list[dict]:
    if not SESSION_DIR.is_dir():
        return []
    sessions = []
    for path in SESSION_DIR.glob("*.json"):
        data = _load_session_file(path.stem)
        if data is None:
            continue
        sessions.append(
            {
                "id": data["id"],
                "title": data.get("title") or _DEFAULT_SESSION_TITLE,
                "created_at": data.get("created_at"),
                "updated_at": data.get("updated_at"),
                "message_count": len(data.get("messages", [])),
            }
        )
    sessions.sort(key=lambda item: str(item.get("updated_at") or ""), reverse=True)
    return sessions


def _session_append_messages(session_id: str, messages: list[dict]) -> bool:
    data = _load_session_file(session_id)
    if data is None:
        return False
    merged = data.get("messages", []) + messages
    data["messages"] = merged[-_MAX_HISTORY_MESSAGES:]
    if not data.get("title") or data.get("title") == _DEFAULT_SESSION_TITLE:
        data["title"] = _session_title_from_messages(merged)
    data["updated_at"] = _utc_now_iso()
    return _save_session_file(session_id, data)


def _rebuild_session_turns(session_id: str) -> dict | None:
    """Load a session and rebuild the in-memory turn context from its messages."""
    data = _load_session_file(session_id)
    if data is None:
        return None
    turns: list[list[str]] = []
    for message in data.get("messages", []):
        content = str(message.get("content", "")).strip()
        if not content:
            continue
        if message.get("role") == "user":
            turns.append([content])
        elif turns:
            turns[-1].append(content)
    chat_turns = [ChatTurn(user_text, ai_text) for user_text, ai_text in turns]
    with _HISTORY_LOCK:
        _SESSION_TURNS.clear()
        _SESSION_TURNS.extend(chat_turns[-_MAX_CONTEXT_TURNS:])
    return data


def migrate_legacy_history() -> None:
    """Import the legacy single history.json into one session on first run."""
    if not HISTORY_FILE.is_file():
        return
    if SESSION_DIR.is_dir() and any(SESSION_DIR.glob("*.json")):
        return
    try:
        messages = _valid_session_messages(json.loads(HISTORY_FILE.read_text(encoding="utf-8")))
    except (OSError, json.JSONDecodeError):
        return
    if not messages:
        return
    session_id = _create_session_file()
    if session_id is None:
        return
    if not _session_append_messages(session_id, messages):
        logger.warning("Could not migrate legacy history into session %s", session_id)
        return
    try:
        HISTORY_FILE.unlink()
    except OSError:
        pass
    logger.info("Migrated legacy history into session %s (%d messages)", session_id, len(messages))


def _voice_selection() -> "VoiceSelection":
    label = load_settings().get("voice_label", _DEFAULT_SETTINGS["voice_label"])
    return voice_selection_from_saved(label)


def _voice_config() -> "VoiceServiceConfig":
    return voice_service_config(_voice_selection(), PROJECT_ROOT)


def voice_available() -> bool:
    config = _voice_config()
    try:
        with socket.create_connection((config.host, config.port), timeout=_VOICE_PROBE_TIMEOUT_SECONDS):
            return True
    except OSError:
        return False


def ensure_voice_service() -> None:
    """Spawn the OmniVoice Flask server on 127.0.0.1:5052 if it is not running."""
    if voice_available():
        return
    with _VOICE_LOCK:
        if voice_available():
            return
        config = _voice_config()
        validate_voice_environment(config)
        pythonw = config.python_path.with_name("pythonw.exe")
        executable = str(pythonw) if pythonw.is_file() else str(config.python_path)
        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        startup_info = None
        if os.name == "nt":
            startup_info = subprocess.STARTUPINFO()
            startup_info.dwFlags |= getattr(subprocess, "STARTF_USESHOWWINDOW", 0)
            startup_info.wShowWindow = subprocess.SW_HIDE
        subprocess.Popen(
            [executable, str(config.server_script)],
            cwd=str(PROJECT_ROOT),
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=creation_flags,
            startupinfo=startup_info,
        )
        deadline = time.monotonic() + VOICE_START_TIMEOUT_SECONDS
        while time.monotonic() < deadline:
            if voice_available():
                return
            time.sleep(0.2)
        raise RuntimeError(
            "OmniVoice voice service did not start. Check the local voice environment (omnivoice_env)."
        )


def _post_voice(path: str, payload: dict, *, timeout: float) -> tuple[int, bytes]:
    config = _voice_config()
    data = json.dumps(payload).encode("utf-8")
    request = Request(
        f"{config.base_url}{path}",
        data=data,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urlopen(request, timeout=timeout) as response:
            return response.status, response.read()
    except HTTPError as error:
        return error.code, error.read(_MAX_ERROR_BODY_BYTES)
    except URLError as error:
        raise RuntimeError("Voice server is offline. Start OmniVoice and try again.") from error


def request_voice_audio(text: str, voice_label: str, speed: float) -> bytes:
    selection = voice_selection_by_label(voice_label) or VOICE_SELECTIONS[0]
    status, body = _post_voice(
        "/tts",
        {"text": text, "voice_profile": selection.profile_id, "speed": speed},
        timeout=_VOICE_TTS_TIMEOUT_SECONDS,
    )
    if status != 200:
        try:
            detail = json.loads(body.decode("utf-8", errors="replace")).get("error", "")
        except json.JSONDecodeError:
            detail = ""
        suffix = f": {detail}" if isinstance(detail, str) and detail else ""
        raise RuntimeError(f"Voice server failed (HTTP {status}){suffix}")
    if not body:
        raise RuntimeError("Voice server returned empty audio.")
    return body


def request_voice_control(path: str, voice_label: str) -> dict:
    selection = voice_selection_by_label(voice_label) or VOICE_SELECTIONS[0]
    status, body = _post_voice(
        path,
        {"voice_profile": selection.profile_id},
        timeout=_VOICE_LIFECYCLE_TIMEOUT_SECONDS,
    )
    if status != 200:
        raise RuntimeError("Voice service rejected lifecycle request.")
    try:
        decoded = json.loads(body.decode("utf-8"))
    except (json.JSONDecodeError, UnicodeDecodeError) as error:
        raise RuntimeError("Voice service returned an invalid lifecycle response.") from error
    if not isinstance(decoded, dict):
        raise RuntimeError("Voice service returned an invalid lifecycle response.")
    return decoded


def apply_saved_settings() -> str:
    """Apply persisted settings to the runner (backend, model, credentials)."""
    settings = load_settings()
    backend = settings.get("backend")
    if backend not in _VALID_BACKENDS:
        return f"No persisted settings to apply (backend={backend!r})"
    api_key = ""
    if backend in (BACKEND_OPENCODE, BACKEND_KILO):
        store = core.opencode_cred if backend == BACKEND_OPENCODE else core.kilo_cred
        try:
            api_key = store.get_key() or ""
        except Exception as error:
            logger.warning("Could not read the saved API key: %s", error)
    core.runner.select_backend(backend)
    return core.runner.load(
        checkpoint_path=settings.get("checkpoint_path", ""),
        api_key=api_key,
        model_name=settings.get("model", _DEFAULT_SETTINGS["model"]),
        api_variant=settings.get("api_variant", _DEFAULT_SETTINGS["api_variant"]),
    )


@app.on_event("startup")
def _apply_settings_on_startup() -> None:
    try:
        migrate_legacy_history()
    except Exception as error:
        logger.error("Failed to migrate legacy history on startup: %s", error)
    try:
        result = apply_saved_settings()
        logger.info("Applied saved settings on startup: %s", result)
    except Exception as error:
        logger.error("Failed to apply saved settings on startup: %s", error)
    try:
        threading.Thread(target=_start_voice_service, name="voice-auto-start", daemon=True).start()
    except Exception as error:
        logger.error("Failed to schedule voice service startup: %s", error)
    try:
        _start_memory_learner()
    except Exception as error:
        logger.error("Failed to start memory learner: %s", error)


def _start_voice_service() -> None:
    try:
        ensure_voice_service()
        logger.info("Voice service is running on startup")
    except Exception as error:
        logger.error("Failed to auto-start voice service: %s", error)


core = MiniGPTCore()


class SettingsPayload(BaseModel):
    backend: str | None = None
    model: str | None = None
    api_key: str = ""
    api_variant: str | None = None
    max_tokens: int | None = None
    temperature: float | None = None
    top_k: int | None = None
    allow_web_search: bool | None = None
    enable_self_learning: bool | None = None
    checkpoint_path: str = ""
    voice_label: str | None = None
    voice_speed: float | None = None


class VoiceTtsPayload(BaseModel):
    text: str
    voice_label: str | None = None
    speed: float | None = None


class TranscribePayload(BaseModel):
    audio_b64: str
    sample_rate: int = 16_000


class VtsActionPayload(BaseModel):
    facial: str | None = None
    body: str | None = None
    codes: str | None = None


@app.get("/status")
async def get_status():
    return {"status": "ok", "message": "Backend is running and ready for Tauri UI."}


@app.get("/settings")
async def get_settings():
    saved = load_settings()
    merged = {**{key: _DEFAULT_SETTINGS.get(key) for key in _DEFAULT_SETTINGS}, **{key: saved[key] for key in _DEFAULT_SETTINGS if key in saved}}
    return {"status": "ok", **merged}


@app.post("/settings")
async def save_settings(payload: SettingsPayload):
    if payload.backend is not None and payload.backend not in _VALID_BACKENDS:
        return {"status": "error", "message": f"unsupported backend: {payload.backend}"}
    settings = load_settings()
    if payload.backend is not None:
        settings["backend"] = payload.backend
    if payload.model is not None:
        settings["model"] = payload.model
    if payload.api_variant is not None:
        settings["api_variant"] = payload.api_variant
    if payload.max_tokens is not None:
        settings["max_tokens"] = payload.max_tokens
    if payload.temperature is not None:
        settings["temperature"] = payload.temperature
    if payload.top_k is not None:
        settings["top_k"] = payload.top_k
    if payload.allow_web_search is not None:
        settings["allow_web_search"] = payload.allow_web_search
    if payload.enable_self_learning is not None:
        settings["enable_self_learning"] = payload.enable_self_learning
    if payload.checkpoint_path.strip():
        settings["checkpoint_path"] = payload.checkpoint_path.strip()
    if payload.voice_label is not None:
        settings["voice_label"] = payload.voice_label
    if payload.voice_speed is not None:
        settings["voice_speed"] = payload.voice_speed
    save_settings_to_file(settings)

    if payload.backend is None:
        return {"status": "success", "message": "Settings saved."}

    if payload.backend == BACKEND_OPENCODE:
        if payload.api_key.strip():
            core.opencode_cred.set(payload.api_key)
    elif payload.backend == BACKEND_KILO:
        if payload.api_key.strip():
            core.kilo_cred.set(payload.api_key)

    core.runner.select_backend(payload.backend)
    try:
        result = core.runner.load(
            checkpoint_path=payload.checkpoint_path or "",
            api_key=payload.api_key,
            model_name=payload.model or "",
            api_variant=payload.api_variant or "",
        )
        return {"status": "success", "message": result}
    except Exception as error:
        logger.error("Failed to load backend %s: %s", payload.backend, error)
        return {"status": "error", "message": str(error)}


@app.delete("/memory")
async def clear_memory():
    core.clear_memory()
    save_history([])
    return {"status": "success", "message": "Memory and history cleared."}


@app.get("/history")
async def get_history():
    return load_history()


@app.get("/sessions")
def sessions_list():
    return {"status": "ok", "sessions": load_sessions()}


@app.post("/sessions/new")
def session_new():
    session_id = _create_session_file()
    if session_id is None:
        return {"status": "error", "message": "Không tạo được session."}
    with _HISTORY_LOCK:
        _SESSION_TURNS.clear()
    return {"status": "ok", "session_id": session_id}


@app.post("/sessions/{session_id}/activate")
def session_activate(session_id: str):
    data = _rebuild_session_turns(session_id)
    if data is None:
        return {"status": "error", "message": "Session không tồn tại."}
    return {
        "status": "ok",
        "session": {
            "id": data["id"],
            "title": data.get("title") or _DEFAULT_SESSION_TITLE,
            "messages": data.get("messages", []),
        },
    }


@app.delete("/sessions/{session_id}")
def session_delete(session_id: str):
    if not _session_id_is_valid(session_id):
        return {"status": "error", "message": "Session không tồn tại."}
    path = _session_path(session_id)
    try:
        path.unlink()
    except FileNotFoundError:
        return {"status": "error", "message": "Session không tồn tại."}
    except OSError as error:
        logger.error("Could not delete session %s: %s", session_id, error)
        return {"status": "error", "message": str(error)}
    return {"status": "ok"}


@app.get("/voice/status")
def voice_status():
    return {"status": "ok", "available": voice_available()}


@app.post("/voice/tts")
def voice_tts(payload: VoiceTtsPayload):
    text = payload.text.strip()
    if not text:
        return {"status": "error", "message": "text is required"}
    settings = load_settings()
    voice_label = payload.voice_label or settings.get("voice_label", _DEFAULT_SETTINGS["voice_label"])
    speed = payload.speed if payload.speed is not None else settings.get("voice_speed", _DEFAULT_SETTINGS["voice_speed"])
    try:
        ensure_voice_service()
        audio = request_voice_audio(text, voice_label, speed)
        return Response(content=audio, media_type="audio/wav")
    except Exception as error:
        logger.error("TTS failed: %s", error)
        return {"status": "error", "message": str(error)}


@app.post("/voice/warmup")
def voice_warmup():
    try:
        ensure_voice_service()
        result = request_voice_control("/warmup", _voice_selection().label)
        return {"status": "ok", "result": result}
    except Exception as error:
        logger.error("Voice warmup failed: %s", error)
        return {"status": "error", "message": str(error)}


@app.post("/voice/release")
def voice_release():
    try:
        result = request_voice_control("/release", _voice_selection().label)
        return {"status": "ok", "result": result}
    except Exception as error:
        logger.error("Voice release failed: %s", error)
        return {"status": "error", "message": str(error)}


@app.post("/voice/transcribe")
def voice_transcribe(payload: TranscribePayload):
    if not payload.audio_b64.strip():
        return {"status": "error", "message": "audio is required"}
    try:
        raw = base64.b64decode(payload.audio_b64, validate=True)
    except (binascii.Error, ValueError):
        return {"status": "error", "message": "invalid audio payload"}
    if not raw:
        return {"status": "error", "message": "audio is empty"}
    if payload.sample_rate <= 0:
        return {"status": "error", "message": "sample rate must be positive"}
    try:
        recognizer = build_speech_recognizer(SpeechEngineConfig(language="vi-VN"))
        text = recognizer.recognize(CapturedAudio(raw, payload.sample_rate, 2))
        return {"status": "ok", "text": text}
    except SpeechRecognitionError as error:
        return {"status": "error", "message": str(error)}
    except Exception as error:
        logger.error("Speech recognition failed: %s", error)
        return {"status": "error", "message": "Speech recognition failed."}


def _vts_manager() -> VTSManager:
    global _VTS_MANAGER, _VTS_THREAD
    if _VTS_MANAGER is None:
        with _VTS_LOCK:
            if _VTS_MANAGER is None:
                manager = VTSManager()
                thread = threading.Thread(target=manager.run, name="vts-loop", daemon=True)
                thread.start()
                _VTS_MANAGER = manager
                _VTS_THREAD = thread
    return _VTS_MANAGER


@app.get("/vts/status")
def vts_status():
    manager = _VTS_MANAGER
    return {"status": "ok", "connected": bool(manager is not None and manager.is_connected)}


@app.post("/vts/connect")
def vts_connect():
    manager = _vts_manager()
    if manager.is_connected:
        return {"status": "ok", "connected": True}
    deadline = time.monotonic() + VTS_CONNECT_TIMEOUT_SECONDS
    while time.monotonic() < deadline:
        if manager.is_connected:
            return {"status": "ok", "connected": True}
        time.sleep(0.5)
    return {
        "status": "error",
        "message": "VTube Studio is not running or did not authenticate. Open VTube Studio, allow Ru, then retry.",
    }


@app.post("/vts/action")
def vts_action(payload: VtsActionPayload):
    manager = _VTS_MANAGER
    if manager is None or not manager.is_connected:
        return {"status": "error", "message": "VTube Studio is not connected."}
    try:
        if payload.codes:
            codes = tuple(code.strip().lower() for code in payload.codes.replace(",", " ").split() if code.strip())
            catalog = manager.current_catalog_sync()
            if catalog is None:
                return {"status": "error", "message": "catalog not loaded yet; retry in a moment."}
            valid = {entry.code for entry in catalog.entries}
            unknown = [code for code in codes if code not in valid]
            if unknown:
                return {"status": "error", "message": f"unknown codes: {', '.join(unknown)}"}
            manager.trigger_catalog_actions_sync(catalog, codes)
            return {"status": "ok"}
        actions = VtsActions(facial=payload.facial or None, body=payload.body or None)
        if actions.facial is None and actions.body is None:
            return {"status": "error", "message": "provide codes, facial, or body"}
        manager.trigger_actions_sync(actions)
        return {"status": "ok"}
    except Exception as error:
        logger.error("VTS action failed: %s", error)
        return {"status": "error", "message": str(error)}


def _trigger_vts_catalog_codes(manager: VTSManager, codes: tuple[str, ...]) -> None:
    try:
        catalog = manager.current_catalog_sync()
        if catalog is None:
            return
        manager.trigger_catalog_actions_sync(catalog, codes)
    except Exception as error:
        logger.debug("Background VTS catalog trigger failed: %s", error)


def _safe_put(loop: asyncio.AbstractEventLoop, queue: asyncio.Queue, message: dict) -> None:
    """Put a chat message from a worker thread, tolerating a closed event loop."""
    try:
        asyncio.run_coroutine_threadsafe(queue.put(message), loop)
    except RuntimeError:
        logger.debug("Event loop closed; dropping chat message %r", message["type"])


def _learn_from_turn(prompt: str, reply: str) -> None:
    if not load_settings().get("enable_self_learning", _DEFAULT_SETTINGS["enable_self_learning"]):
        return
    try:
        _MEMORY_QUEUE.put_nowait((prompt, reply))
    except queue.Full:
        try:
            _MEMORY_QUEUE.get_nowait()
        except queue.Empty:
            pass
        try:
            _MEMORY_QUEUE.put_nowait((prompt, reply))
        except queue.Full:
            logger.debug("Memory queue full; dropping learning turn")


def _process_memory_turn(prompt: str, reply: str) -> None:
    if not _MEMORY_TURN_LOCK.acquire(blocking=False):
        logger.debug("Model busy; skipping self-learning turn")
        return
    try:
        if is_sensitive_or_unsafe(prompt) or is_sensitive_or_unsafe(reply):
            logger.debug("Bỏ qua self-learning vì lượt trò chuyện nhạy cảm")
            return
        facts = extract_facts_with_llm(
            prompt,
            reply,
            lambda fact_prompt, max_new_tokens, temperature, top_k, allow_web_search: core.runner.generate_reply(
                prompt=fact_prompt,
                max_new_tokens=max_new_tokens,
                temperature=temperature,
                top_k=top_k,
                allow_web_search=allow_web_search,
            ),
        )
        store = core.get_memory_store()
        existing = {record.content.casefold() for record in store.list_active()}
        for fact in facts:
            if fact.content.casefold() in existing:
                continue
            store.upsert(
                fact.kind,
                fact.content,
                scope=None,
                evidence=prompt[:2000],
                confidence=fact.confidence,
                importance=fact.importance,
            )
            existing.add(fact.content.casefold())
    except Exception as error:
        logger.warning("Self-learning failed: %s", error)
    finally:
        _MEMORY_TURN_LOCK.release()


def _memory_learner_loop() -> None:
    while True:
        try:
            prompt, reply = _MEMORY_QUEUE.get()
        except Exception:
            continue
        try:
            _process_memory_turn(prompt, reply)
        except Exception as error:
            logger.warning("Self-learning worker error: %s", error)


def _start_memory_learner() -> None:
    if _MEMORY_WORKER_STARTED.is_set():
        return
    _MEMORY_WORKER_STARTED.set()
    threading.Thread(target=_memory_learner_loop, name="memory-learner", daemon=True).start()


@app.websocket("/chat")
async def chat_endpoint(websocket: WebSocket):
    await websocket.accept()

    loop = asyncio.get_event_loop()
    chat_queue = asyncio.Queue()

    def stream_callback(delta: str):
        _safe_put(loop, chat_queue, {"type": "chunk", "content": delta})

    def run_generation(prompt: str, session_id: str):
        full_reply = None
        try:
            settings = load_settings()
            _MEMORY_TURN_LOCK.acquire()
            if not _session_append_messages(session_id, [{"role": "user", "content": prompt}]):
                _safe_put(
                    loop,
                    chat_queue,
                    {"type": "error", "content": "Session không tồn tại hoặc không lưu được.", "session_id": session_id},
                )
                return

            full_reply = core.generate_reply_with_memory(
                prompt=prompt,
                max_new_tokens=settings.get("max_tokens", _DEFAULT_SETTINGS["max_tokens"]),
                temperature=settings.get("temperature", _DEFAULT_SETTINGS["temperature"]),
                top_k=settings.get("top_k", _DEFAULT_SETTINGS["top_k"]),
                allow_web_search=settings.get("allow_web_search", _DEFAULT_SETTINGS["allow_web_search"]),
                turns_history=list(_SESSION_TURNS),
                on_stream_delta=stream_callback,
            )

            core.remember(prompt, full_reply)

            _session_append_messages(session_id, [{"role": "ai", "content": full_reply}])

            with _HISTORY_LOCK:
                _SESSION_TURNS.append(ChatTurn(prompt, full_reply))
                del _SESSION_TURNS[:-_MAX_CONTEXT_TURNS]

            manager = _VTS_MANAGER
            if manager is not None and manager.is_connected:
                try:
                    catalog = manager.current_catalog_sync()
                    if catalog is not None:
                        _, codes = parse_catalog_reply(full_reply, catalog)
                        if codes:
                            threading.Thread(
                                target=_trigger_vts_catalog_codes,
                                args=(manager, codes),
                                daemon=True,
                            ).start()
                except Exception as error:
                    logger.debug("VTS marker parse failed: %s", error)

            _safe_put(loop, chat_queue, {"type": "done", "content": full_reply, "session_id": session_id})
        except Exception as error:
            logger.exception("Chat generation failed for prompt %r", prompt[:80])
            _safe_put(loop, chat_queue, {"type": "error", "content": str(error), "session_id": session_id})
        finally:
            _MEMORY_TURN_LOCK.release()
            _TURN_LOCK.release()
            if full_reply is not None:
                _learn_from_turn(prompt, full_reply)

    try:
        while True:
            data = await websocket.receive_text()
            req = json.loads(data)
            prompt = str(req.get("prompt", "")).strip()
            if not prompt:
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


if __name__ == "__main__":
    uvicorn.run("mini_gpt.server:app", host="127.0.0.1", port=8000)
