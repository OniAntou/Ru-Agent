from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
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
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request as UrlRequest, urlopen

from mini_gpt._atomic_write import atomic_write_json
from mini_gpt.backend_core import MiniGPTCore
from mini_gpt.chat import ChatTurn
from mini_gpt.gpu_lifecycle import GpuLifecycle, VoiceServiceController
from mini_gpt.headless_runner import (
    BACKEND_GGUF,
    BACKEND_KILO,
    BACKEND_OPENCODE,
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
from mini_gpt.vts_config import MotionConfig
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

def _valid_desktop_token(candidate: str) -> bool:
    # Read the token from the environment on every call: the WinUI3 host sets
    # RU_DESKTOP_TOKEN only after this module has been imported (it launches via
    # `python -m mini_gpt.server --secret-token ...`), so a module-level capture
    # would leave API auth permanently disabled.
    token = os.environ.get("RU_DESKTOP_TOKEN", "").strip()
    if not token:
        logger.warning("API auth is disabled: RU_DESKTOP_TOKEN is not set")
        return True
    return hmac.compare_digest(candidate, token)


# ── paths / files ─────────────────────────────────────────────────────

SETTINGS_FILE = Path.home() / ".minigpt" / "settings.json"
HISTORY_FILE = Path.home() / ".minigpt" / "history.json"
SESSION_DIR = Path.home() / ".minigpt" / "sessions"
PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent

# ── constants ─────────────────────────────────────────────────────────

_MAX_HISTORY_MESSAGES = 200
_MAX_CONTEXT_TURNS = 8
_MAX_SESSION_TITLE_CHARS = 50
_DEFAULT_SESSION_TITLE = "Cuộc trò chuyện mới"
_SESSION_ID_RE = re.compile(r"^[a-zA-Z0-9_-]{1,64}$")
_MAX_PROMPT_CHARS = 16_000
_MAX_WS_MESSAGE_BYTES = 256 * 1024

VOICE_START_TIMEOUT_SECONDS = 45.0
_VOICE_PROBE_TIMEOUT_SECONDS = 0.25
_VOICE_TTS_TIMEOUT_SECONDS = 90.0
_VOICE_LIFECYCLE_TIMEOUT_SECONDS = 30.0
VTS_CONNECT_TIMEOUT_SECONDS = 20.0
_MAX_ERROR_BODY_BYTES = 4096
MAX_REQUEST_BODY_BYTES = 8 * 1024 * 1024

# The native WinUI3 host is not a browser; no Origin is sent, so no CORS
# origins are allowed (a cross-origin browser page can never read responses).
_DESKTOP_ORIGINS: tuple[str, ...] = ()

_DEFAULT_SETTINGS = {
    "backend": BACKEND_GGUF,
    "model": "",
    "opencode_model": "",
    "kilo_model": "",
    "api_variant": "Default",
    "max_tokens": 256,
    "temperature": 0.7,
    "top_k": 50,
    "allow_web_search": True,
    "enable_self_learning": True,
    "share_memory_with_cloud": False,
    "checkpoint_path": "",
    "voice_label": "Ru (OmniVoice)",
    "voice_speed": 1.0,
}

_VALID_BACKENDS = (BACKEND_GGUF, BACKEND_OPENCODE, BACKEND_KILO)

_BACKEND_ALIASES: dict[str, str] = {
    "gguf": BACKEND_GGUF,
    "opencode": BACKEND_OPENCODE,
    "kilo": BACKEND_KILO,
}

# ── shared state ──────────────────────────────────────────────────────

_HISTORY_LOCK = threading.Lock()
_TURN_LOCK = threading.Lock()
_VOICE_LOCK = threading.Lock()
_GPU_LOCK = threading.Lock()
_VTS_LOCK = threading.Lock()
_MEMORY_TURN_LOCK = threading.Lock()
_PENDING_REACTION_LOCK = threading.Lock()

_MEMORY_QUEUE_MAXSIZE = 50
_MEMORY_LOCK_TIMEOUT_SECONDS = 10.0
_MEMORY_QUEUE: queue.Queue = queue.Queue(maxsize=_MEMORY_QUEUE_MAXSIZE)
_MEMORY_WORKER_STARTED = threading.Event()
_MEMORY_STOP = threading.Event()

_SCREEN_REACTIONS = ReactionCoordinator()
_PENDING_REACTION_TEXT: dict[str, tuple[str, float]] = {}

_VTS_MANAGER: VTSManager | None = None
_VTS_THREAD: threading.Thread | None = None
_VOICE_PROCESS: subprocess.Popen | None = None
_GPU_LIFECYCLE: GpuLifecycle | None = None

core = MiniGPTCore()

# ── helpers ───────────────────────────────────────────────────────────

def _resolve_backend(value: str | None) -> str | None:
    if value is None:
        return None
    return _BACKEND_ALIASES.get(value, value)


def _utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def _session_id_is_valid(session_id: str) -> bool:
    return bool(_SESSION_ID_RE.fullmatch(session_id))


def _session_path(session_id: str) -> Path:
    return SESSION_DIR / f"{session_id}.json"


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


def _session_title_from_messages(messages: list[dict]) -> str:
    for message in messages:
        if message.get("role") == "user":
            text = " ".join(str(message.get("content", "")).split())
            if text:
                return text[:_MAX_SESSION_TITLE_CHARS]
    return _DEFAULT_SESSION_TITLE


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

    backend = saved.get("backend", _DEFAULT_SETTINGS["backend"])
    if backend == BACKEND_KILO and not merged.get("kilo_model"):
        merged["kilo_model"] = saved.get("model", "")
    elif backend == BACKEND_OPENCODE and not merged.get("opencode_model"):
        merged["opencode_model"] = saved.get("model", "")

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


def _new_session_id() -> str:
    from uuid import uuid4
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


# ── voice helpers ─────────────────────────────────────────────────────

_VOICE_MARKERS_RE = re.compile(r"\s*\[\[(?:VTS|VOICE_PACE):[^\]]*\]\]\s*")


def _strip_voice_markers(text: str) -> str:
    return _VOICE_MARKERS_RE.sub(" ", text).strip()


def _voice_selection() -> "VoiceSelection":
    label = effective_settings()["voice_label"]
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
    request = UrlRequest(
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
    try:
        decoded = json.loads(body.decode("utf-8"))
    except (json.JSONDecodeError, UnicodeDecodeError) as error:
        raise RuntimeError("Voice service returned an invalid lifecycle response.") from error
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
    settings = load_settings()
    backend = _resolve_backend(settings.get("backend"))
    logger.info("Loading settings: backend=%s, raw_backend=%s", backend, settings.get("backend"))
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

    model_name = ""
    if backend == BACKEND_OPENCODE:
        model_name = settings.get("opencode_model") or settings.get("model", _DEFAULT_SETTINGS["opencode_model"])
    elif backend == BACKEND_KILO:
        model_name = settings.get("kilo_model") or settings.get("model", _DEFAULT_SETTINGS["kilo_model"])
        logger.info("Using Kilo model: %s", model_name)
    else:
        model_name = settings.get("model", _DEFAULT_SETTINGS["model"])
        logger.info("Using local model field: %s", model_name)

    checkpoint_path = settings.get("checkpoint_path", "")
    logger.info("Loading with checkpoint_path=%s, model_name=%s", checkpoint_path, model_name)

    return core.runner.load(
        checkpoint_path=checkpoint_path,
        api_key=api_key,
        model_name=model_name,
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


# ── VTS helpers ───────────────────────────────────────────────────────

def _vts_manager() -> VTSManager:
    global _VTS_MANAGER, _VTS_THREAD
    if _VTS_MANAGER is None:
        with _VTS_LOCK:
            if _VTS_MANAGER is None:
                manager = VTSManager()
                manager.apply_config(_load_vts_motion_config())
                thread = threading.Thread(target=manager.run, name="vts-loop", daemon=True)
                thread.start()
                _VTS_MANAGER = manager
                _VTS_THREAD = thread
    return _VTS_MANAGER


def _trigger_vts_catalog_codes(
    manager: VTSManager, catalog: VtsCatalog, codes: tuple[str, ...]
) -> None:
    try:
        manager.trigger_catalog_actions_sync(catalog, codes)
    except Exception as error:
        logger.debug("Background VTS catalog trigger failed: %s", error)


def _load_vts_motion_config() -> MotionConfig:
    stored = load_settings().get("vts_motion") or {}
    try:
        return MotionConfig.from_dict(stored)
    except ValueError as error:
        logger.warning("Ignoring invalid stored vts_motion config: %s", error)
        return MotionConfig.defaults()


def _save_vts_motion_config(config: MotionConfig) -> None:
    settings = load_settings()
    settings["vts_motion"] = config.to_dict()
    save_settings_to_file(settings)


# ── memory / self-learning ────────────────────────────────────────────

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


# ── chat helpers ──────────────────────────────────────────────────────

def _safe_put(loop: asyncio.AbstractEventLoop, queue: asyncio.Queue, message: dict) -> None:
    try:
        asyncio.run_coroutine_threadsafe(queue.put(message), loop)
    except RuntimeError:
        logger.debug("Event loop closed; dropping chat message %r", message["type"])


# ── lifespan ──────────────────────────────────────────────────────────

@asynccontextmanager
async def _application_lifespan(_app):
    _apply_settings_on_startup()
    try:
        yield
    finally:
        _shutdown_runtime()