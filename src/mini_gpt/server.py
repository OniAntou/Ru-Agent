import asyncio
import base64
import binascii
import hmac
import json
import logging
import os
import queue
import re
import socket
import subprocess
import threading
import time
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Annotated
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
from uuid import uuid4

import uvicorn
from fastapi import FastAPI, Request, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel, Field

from mini_gpt._atomic_write import atomic_write_json
from mini_gpt._text_utils import append_stream_text, safe_streamed_text
from mini_gpt.backend_core import MiniGPTCore
from mini_gpt.chat import ChatTurn
from mini_gpt.gpu_lifecycle import GpuLifecycle, VoiceServiceController
from mini_gpt.headless_runner import (
    BACKEND_GGUF,
    BACKEND_KILO,
    BACKEND_OPENCODE,
    BACKEND_TRANSFORMERS,
)
from mini_gpt.memory import MemoryKind
from mini_gpt.memory_extraction import is_sensitive_or_unsafe
from mini_gpt.memory_learning import ExtractFact, extract_facts_with_llm
from mini_gpt.openai_runtime import OpenAIGenerationCancelled
from mini_gpt.screen_reaction import (
    ReactionCoordinator,
    ReactionRequest,
    VisionObservation,
    format_observation_prompt,
)
from mini_gpt.speech_runtime import (
    CapturedAudio,
    SpeechEngineConfig,
    SpeechRecognitionError,
    build_speech_recognizer,
)
from mini_gpt.vts_actions import VtsActions
from mini_gpt.vts_catalog import VtsCatalog, catalog_instruction, parse_catalog_reply
from mini_gpt.vts_manager import VTSManager
from mini_gpt.voice_pacing import effective_voice_speed, voice_pace_for
from mini_gpt.voice_runtime import (
    VOICE_SELECTIONS,
    voice_selection_by_label,
    voice_selection_from_saved,
    voice_service_config,
    validate_voice_environment,
)

logger = logging.getLogger(__name__)

@asynccontextmanager
async def _application_lifespan(_app: FastAPI):
    _apply_settings_on_startup()
    try:
        yield
    finally:
        _shutdown_runtime()


app = FastAPI(title="MiniGPT Headless Backend", lifespan=_application_lifespan)

_DESKTOP_ORIGINS = (
    "http://localhost:1420",
    "http://127.0.0.1:1420",
    "http://tauri.localhost",
    "tauri://localhost",
)
_DESKTOP_TOKEN = os.environ.get("RU_DESKTOP_TOKEN", "").strip()
MAX_REQUEST_BODY_BYTES = 8 * 1024 * 1024

app.add_middleware(
    CORSMiddleware,
    allow_origins=list(_DESKTOP_ORIGINS),
    allow_credentials=False,
    allow_methods=["GET", "POST", "DELETE", "OPTIONS"],
    allow_headers=["Authorization", "Content-Type"],
)


def _valid_desktop_token(candidate: str) -> bool:
    if not _DESKTOP_TOKEN:
        return True
    return hmac.compare_digest(candidate, _DESKTOP_TOKEN)


@app.middleware("http")
async def desktop_boundary(request: Request, call_next):
    if request.method != "OPTIONS":
        authorization = request.headers.get("authorization", "")
        candidate = authorization.removeprefix("Bearer ").strip()
        if not _valid_desktop_token(candidate):
            return JSONResponse({"detail": "Unauthorized"}, status_code=401)

    content_length = request.headers.get("content-length")
    if content_length:
        try:
            if int(content_length) > MAX_REQUEST_BODY_BYTES:
                return JSONResponse({"detail": "Request body too large"}, status_code=413)
        except ValueError:
            return JSONResponse({"detail": "Invalid Content-Length"}, status_code=400)
    if request.method in {"POST", "PUT", "PATCH"}:
        body = await request.body()
        if len(body) > MAX_REQUEST_BODY_BYTES:
            return JSONResponse({"detail": "Request body too large"}, status_code=413)
    return await call_next(request)

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
_GPU_LOCK = threading.Lock()
_VTS_LOCK = threading.Lock()
_MEMORY_QUEUE_MAXSIZE = 50
_MEMORY_TURN_LOCK = threading.Lock()
_SCREEN_REACTIONS = ReactionCoordinator()
_MEMORY_QUEUE: queue.Queue = queue.Queue(maxsize=_MEMORY_QUEUE_MAXSIZE)
_MEMORY_WORKER_STARTED = threading.Event()
_MEMORY_STOP = threading.Event()

_VTS_MANAGER: VTSManager | None = None
_VTS_THREAD: threading.Thread | None = None
_VOICE_PROCESS: subprocess.Popen | None = None
_GPU_LIFECYCLE: GpuLifecycle | None = None

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
    "share_memory_with_cloud": False,
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


def effective_settings() -> dict:
    saved = load_settings()
    merged = {
        **_DEFAULT_SETTINGS,
        **{key: saved[key] for key in _DEFAULT_SETTINGS if key in saved},
    }

    def bounded_number(key: str, lower: float, upper: float) -> None:
        value = merged.get(key)
        if not isinstance(value, (int, float)) or isinstance(value, bool):
            merged[key] = _DEFAULT_SETTINGS[key]
            return
        merged[key] = max(lower, min(upper, value))

    bounded_number("max_tokens", 1, 1024)
    bounded_number("temperature", 0.1, 2.0)
    bounded_number("top_k", 1, 200)
    bounded_number("voice_speed", 0.5, 2.0)
    merged["max_tokens"] = int(merged["max_tokens"])
    merged["top_k"] = int(merged["top_k"])
    for key in ("allow_web_search", "enable_self_learning", "share_memory_with_cloud"):
        if not isinstance(merged.get(key), bool):
            merged[key] = _DEFAULT_SETTINGS[key]
    return merged


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
    """Load one session; generation derives context from this file per request."""
    return _load_session_file(session_id)


def _session_turns(session_id: str) -> list[ChatTurn]:
    data = _load_session_file(session_id)
    if data is None:
        return []
    pending_prompt: str | None = None
    turns: list[ChatTurn] = []
    for message in data.get("messages", []):
        content = str(message.get("content", "")).strip()
        if not content:
            continue
        if message.get("role") == "user":
            pending_prompt = content
        elif pending_prompt is not None:
            turns.append(ChatTurn(pending_prompt, content))
            pending_prompt = None
    return turns[-_MAX_CONTEXT_TURNS:]


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
    label = effective_settings()["voice_label"]
    return voice_selection_from_saved(label)


_VOICE_MARKERS_RE = re.compile(r"\s*\[\[(?:VTS|VOICE_PACE):[^\]]*\]\]\s*")


def _strip_voice_markers(text: str) -> str:
    """Remove internal VTS/VOICE_PACE markers before sending text to the TTS engine."""
    return _VOICE_MARKERS_RE.sub(" ", text).strip()


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
        global _VOICE_PROCESS
        _VOICE_PROCESS = subprocess.Popen(
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
            if _VOICE_PROCESS.poll() is not None:
                raise RuntimeError("OmniVoice process exited before opening port 5052")
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


def _voice_lifecycle_request(path: str, payload: dict[str, str] | None) -> dict:
    status, body = _post_voice(
        path,
        payload or {},
        timeout=_VOICE_LIFECYCLE_TIMEOUT_SECONDS,
    )
    if status != 200:
        raise RuntimeError("Voice service rejected lifecycle request.")
    decoded = json.loads(body.decode("utf-8"))
    if not isinstance(decoded, dict):
        raise RuntimeError("Voice service returned an invalid lifecycle response.")
    return decoded


def _gpu_lifecycle() -> GpuLifecycle:
    global _GPU_LIFECYCLE
    if _GPU_LIFECYCLE is None:
        _GPU_LIFECYCLE = GpuLifecycle(
            _GPU_LOCK,
            release_local_runtime=core.runner.release_gpu,
            voice_services={
                "omnivoice": VoiceServiceController(
                    available=voice_available,
                    ensure=ensure_voice_service,
                    request=_voice_lifecycle_request,
                )
            },
        )
    return _GPU_LIFECYCLE


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
        _start_memory_learner()
    except Exception as error:
        logger.error("Failed to start memory learner: %s", error)


def _start_voice_service() -> None:
    try:
        ensure_voice_service()
        logger.info("Voice service is running on startup")
    except Exception as error:
        logger.error("Failed to auto-start voice service: %s", error)


def _stop_owned_voice_service() -> None:
    global _VOICE_PROCESS
    process = _VOICE_PROCESS
    _VOICE_PROCESS = None
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def _shutdown_runtime() -> None:
    _MEMORY_STOP.set()
    _MEMORY_WORKER_STARTED.clear()
    manager = _VTS_MANAGER
    if manager is not None and callable(getattr(manager, "shutdown", None)):
        manager.shutdown()
    _stop_owned_voice_service()
    core.close()


core = MiniGPTCore()


class SettingsPayload(BaseModel):
    backend: str | None = Field(default=None, max_length=64)
    model: str | None = Field(default=None, max_length=256)
    api_key: str = Field(default="", max_length=4096)
    api_variant: str | None = Field(default=None, max_length=64)
    max_tokens: int | None = Field(default=None, ge=1, le=1024)
    temperature: float | None = Field(default=None, ge=0.1, le=2.0)
    top_k: int | None = Field(default=None, ge=1, le=200)
    allow_web_search: bool | None = None
    enable_self_learning: bool | None = None
    share_memory_with_cloud: bool | None = None
    checkpoint_path: str = Field(default="", max_length=1024)
    voice_label: str | None = Field(default=None, max_length=128)
    voice_speed: float | None = Field(default=None, ge=0.5, le=2.0)


class VoiceTtsPayload(BaseModel):
    text: str = Field(max_length=12_000)
    voice_label: str | None = Field(default=None, max_length=128)
    speed: float | None = Field(default=None, ge=0.5, le=2.0)


class TranscribePayload(BaseModel):
    audio_b64: str = Field(max_length=7_500_000)
    sample_rate: int = Field(default=16_000, ge=8_000, le=192_000)


class VtsActionPayload(BaseModel):
    facial: str | None = Field(default=None, max_length=64)
    body: str | None = Field(default=None, max_length=64)
    codes: str | None = Field(default=None, max_length=256)


class VtsSpeakingPayload(BaseModel):
    speaking: bool


class VisionObservationPayload(BaseModel):
    scene: str = Field(max_length=500)
    event: str = Field(max_length=2_000)
    confidence: float = Field(ge=0.0, le=1.0)
    novelty: float = Field(ge=0.0, le=1.0)
    comment_worthiness: float = Field(ge=0.0, le=1.0)
    visible_text: list[Annotated[str, Field(max_length=200)]] = Field(
        default_factory=list, max_length=16
    )


class ObservationReactionPayload(BaseModel):
    observation_id: str = Field(pattern=r"^[a-zA-Z0-9_-]{1,64}$")
    event_age_ms: int = Field(ge=0, le=60_000)
    observation: VisionObservationPayload


class ObservationCancelPayload(BaseModel):
    observation_id: str = Field(pattern=r"^[a-zA-Z0-9_-]{1,64}$")


@app.get("/status")
async def get_status():
    return {"status": "ok", "message": "Backend is running and ready for Tauri UI."}


@app.get("/models")
async def get_models(backend: str = ""):
    if backend not in (BACKEND_OPENCODE, BACKEND_KILO):
        return {"status": "error", "message": f"unsupported backend: {backend!r}"}
    from mini_gpt.openai_runtime import KILO_GATEWAY_BASE_URL, OPENCODE_BASE_URL

    cred = core.opencode_cred if backend == BACKEND_OPENCODE else core.kilo_cred
    try:
        api_key = cred.get_key() or ""
    except Exception as error:
        logger.warning("Could not read the saved API key: %s", error)
        api_key = ""
    if not api_key:
        return {"status": "error", "message": "API key is not set. Save it in Model Settings first."}
    base_url = OPENCODE_BASE_URL if backend == BACKEND_OPENCODE else KILO_GATEWAY_BASE_URL
    try:
        from mini_gpt.openai_runtime import OpenAIRuntime

        models = OpenAIRuntime.fetch_models(api_key, base_url)
    except Exception as error:
        logger.warning("Failed to fetch models for %s: %s", backend, error)
        return {"status": "error", "message": str(error)}
    return {"status": "ok", "models": [model["id"] for model in models]}


@app.post("/observation/preflight")
def observation_preflight():
    if core.runner.backend not in (BACKEND_OPENCODE, BACKEND_KILO):
        return {
            "status": "error",
            "message": "Screen reactions require an active OpenCode or Kilo cloud backend.",
        }
    try:
        selection = _voice_selection()
        _gpu_lifecycle().prepare_speech(selection)
        settings = effective_settings()
        return {
            "status": "ok",
            "voice_label": selection.label,
            "voice_speed": float(
                settings.get("voice_speed", _DEFAULT_SETTINGS["voice_speed"])
            ),
        }
    except Exception as error:
        logger.warning("Observation voice preflight failed: %s", type(error).__name__)
        return {"status": "error", "message": str(error)}


@app.post("/observation/react")
def observation_react(payload: ObservationReactionPayload):
    request = ReactionRequest(
        observation_id=payload.observation_id,
        event_age_ms=payload.event_age_ms,
        observation=VisionObservation(
            scene=payload.observation.scene,
            event=payload.observation.event,
            confidence=payload.observation.confidence,
            novelty=payload.observation.novelty,
            comment_worthiness=payload.observation.comment_worthiness,
            visible_text=tuple(payload.observation.visible_text),
        ),
    )
    decision = _SCREEN_REACTIONS.begin(request)
    if not decision.allowed:
        return {"status": "skipped", "reason": decision.reason}
    if not _TURN_LOCK.acquire(blocking=False):
        _SCREEN_REACTIONS.fail(payload.observation_id)
        return {"status": "skipped", "reason": "chat_busy"}
    if not _MEMORY_TURN_LOCK.acquire(blocking=False):
        _TURN_LOCK.release()
        _SCREEN_REACTIONS.fail(payload.observation_id)
        return {"status": "skipped", "reason": "chat_busy"}

    try:
        manager = _VTS_MANAGER
        catalog = None
        if manager is not None and manager.is_connected:
            try:
                catalog = manager.current_catalog_sync()
            except Exception as error:
                logger.debug("VTS catalog read failed for screen reaction: %s", type(error).__name__)

        timing = {"seconds": None}
        started = time.perf_counter()

        def record_timing(seconds: float) -> None:
            timing["seconds"] = max(0.0, seconds)

        raw_reply = core.runner.generate_screen_reaction(
            format_observation_prompt(
                request.observation, _SCREEN_REACTIONS.recent_reactions()
            ),
            vts_instruction=catalog_instruction(catalog),
            is_cancelled=lambda: _SCREEN_REACTIONS.is_cancelled(payload.observation_id),
            on_complete_timing=record_timing,
        )
        pace = voice_pace_for(raw_reply)
        cleaned, codes = parse_catalog_reply(raw_reply, catalog)
        cleaned = _strip_voice_markers(cleaned)
        if not cleaned:
            _SCREEN_REACTIONS.fail(payload.observation_id)
            return {"status": "skipped", "reason": "empty"}
        if _SCREEN_REACTIONS.is_cancelled(payload.observation_id):
            _SCREEN_REACTIONS.fail(payload.observation_id)
            return {"status": "skipped", "reason": "superseded"}
        if manager is not None and manager.is_connected and catalog is not None and codes:
            _trigger_vts_catalog_codes(manager, catalog, codes)

        selection = _voice_selection()
        settings = effective_settings()
        voice_speed = effective_voice_speed(
            settings.get("voice_speed", _DEFAULT_SETTINGS["voice_speed"]),
            auto_pace_enabled=True,
            pace=pace,
        )
        cloud_seconds = timing["seconds"]
        if cloud_seconds is None:
            cloud_seconds = max(0.0, time.perf_counter() - started)
        _SCREEN_REACTIONS.complete(payload.observation_id, cleaned)
        return {
            "status": "ok",
            "reaction": {
                "observation_id": payload.observation_id,
                "text": cleaned,
                "voice_label": selection.label,
                "voice_speed": voice_speed,
                "cloud_ms": int(round(cloud_seconds * 1000)),
            },
        }
    except OpenAIGenerationCancelled:
        _SCREEN_REACTIONS.fail(payload.observation_id)
        return {"status": "skipped", "reason": "superseded"}
    except Exception as error:
        _SCREEN_REACTIONS.fail(payload.observation_id)
        logger.warning("Screen reaction generation failed: %s", type(error).__name__)
        return {"status": "error", "message": str(error)}
    finally:
        _MEMORY_TURN_LOCK.release()
        _TURN_LOCK.release()


@app.post("/observation/cancel")
def observation_cancel(payload: ObservationCancelPayload):
    _SCREEN_REACTIONS.cancel(payload.observation_id)
    return {"status": "ok"}


@app.get("/settings")
async def get_settings():
    return {"status": "ok", **effective_settings()}


@app.post("/settings")
async def save_settings(payload: SettingsPayload):
    if payload.backend is not None and payload.backend not in _VALID_BACKENDS:
        return {"status": "error", "message": f"unsupported backend: {payload.backend}"}
    settings = effective_settings()
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
    if payload.share_memory_with_cloud is not None:
        settings["share_memory_with_cloud"] = payload.share_memory_with_cloud
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
            core.opencode_cred.set_key(payload.api_key)
    elif payload.backend == BACKEND_KILO:
        if payload.api_key.strip():
            core.kilo_cred.set_key(payload.api_key)

    core.runner.select_backend(payload.backend)
    api_key = payload.api_key
    if payload.backend in (BACKEND_OPENCODE, BACKEND_KILO) and not api_key.strip():
        store = core.opencode_cred if payload.backend == BACKEND_OPENCODE else core.kilo_cred
        try:
            api_key = store.get_key() or ""
        except Exception as error:
            logger.warning("Could not read the saved API key: %s", error)
    try:
        result = core.runner.load(
            checkpoint_path=payload.checkpoint_path or "",
            api_key=api_key,
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
    return {
        "status": "success",
        "message": "Structured memory cleared. Session history was retained.",
    }


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
    settings = effective_settings()
    voice_label = payload.voice_label or settings.get("voice_label", _DEFAULT_SETTINGS["voice_label"])
    speed = payload.speed if payload.speed is not None else settings.get("voice_speed", _DEFAULT_SETTINGS["voice_speed"])
    pace = voice_pace_for(text)
    speed = effective_voice_speed(speed, auto_pace_enabled=True, pace=pace)
    text = _strip_voice_markers(text)
    try:
        _gpu_lifecycle().prepare_speech(voice_selection_from_saved(voice_label))
        audio = request_voice_audio(text, voice_label, speed)
        return Response(content=audio, media_type="audio/wav")
    except Exception as error:
        logger.error("TTS failed: %s", error)
        return {"status": "error", "message": str(error)}


@app.post("/voice/warmup")
def voice_warmup():
    try:
        selection = _voice_selection()
        _gpu_lifecycle().prepare_speech(selection)
        result = {"ready": True, "voice_profile": selection.profile_id}
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
        if not manager.trigger_actions_sync(actions):
            return {"status": "error", "message": "requested action is unavailable or cooling down"}
        return {"status": "ok"}
    except Exception as error:
        logger.error("VTS action failed: %s", error)
        return {"status": "error", "message": str(error)}


@app.post("/vts/speaking")
def vts_speaking(payload: VtsSpeakingPayload):
    manager = _VTS_MANAGER
    if manager is None or not manager.is_connected:
        return {"status": "ok", "connected": False}
    manager.set_speaking(payload.speaking)
    return {"status": "ok", "connected": True}


def _trigger_vts_catalog_codes(
    manager: VTSManager, catalog: VtsCatalog, codes: tuple[str, ...]
) -> None:
    try:
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
    if not effective_settings()["enable_self_learning"]:
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
    while not _MEMORY_STOP.is_set():
        try:
            prompt, reply = _MEMORY_QUEUE.get(timeout=0.5)
        except queue.Empty:
            continue
        try:
            _process_memory_turn(prompt, reply)
        except Exception as error:
            logger.warning("Self-learning worker error: %s", error)


def _start_memory_learner() -> None:
    if _MEMORY_WORKER_STARTED.is_set():
        return
    _MEMORY_STOP.clear()
    _MEMORY_WORKER_STARTED.set()
    threading.Thread(target=_memory_learner_loop, name="memory-learner", daemon=True).start()


@app.websocket("/chat")
async def chat_endpoint(websocket: WebSocket):
    if not _valid_desktop_token(websocket.query_params.get("token", "")):
        await websocket.close(code=4401, reason="Unauthorized")
        return
    await websocket.accept()

    loop = asyncio.get_event_loop()
    chat_queue = asyncio.Queue()
    stream_state = {"raw": "", "visible": ""}

    def stream_callback(delta: str):
        raw = append_stream_text(stream_state["raw"], delta)
        visible = safe_streamed_text(raw)
        previous = stream_state["visible"]
        stream_state["raw"] = raw
        stream_state["visible"] = visible
        if visible.startswith(previous) and len(visible) > len(previous):
            _safe_put(
                loop,
                chat_queue,
                {"type": "chunk", "content": visible[len(previous) :]},
            )

    def stream_reset_callback():
        stream_state["raw"] = ""
        stream_state["visible"] = ""
        _safe_put(loop, chat_queue, {"type": "reset"})

    def run_generation(prompt: str, session_id: str):
        full_reply = None
        try:
            settings = effective_settings()
            _MEMORY_TURN_LOCK.acquire()
            turns_history = _session_turns(session_id)
            if not _session_append_messages(session_id, [{"role": "user", "content": prompt}]):
                _safe_put(
                    loop,
                    chat_queue,
                    {"type": "error", "content": "Session không tồn tại hoặc không lưu được.", "session_id": session_id},
                )
                return

            manager = _VTS_MANAGER
            catalog = None
            if manager is not None and manager.is_connected:
                try:
                    catalog = manager.current_catalog_sync()
                except Exception as error:
                    logger.debug("VTS catalog read failed: %s", error)

            if core.runner.backend not in (BACKEND_OPENCODE, BACKEND_KILO):
                _gpu_lifecycle().prepare_local_generation()
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
            if len(prompt) > 16_000:
                await websocket.send_text(
                    json.dumps({"type": "error", "content": "Prompt too long (max 16000 characters)."})
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


if __name__ == "__main__":
    uvicorn.run("mini_gpt.server:app", host="127.0.0.1", port=8000)
