from __future__ import annotations

import asyncio
import contextlib
from contextlib import asynccontextmanager, nullcontext
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

from ru_runtime.persistence.atomic_write import atomic_write_json
from ru_runtime.persistence.canonical import canonical_data_root
from ru_runtime.worker.v1.core import RuRuntimeCore
from ru_runtime.generation.chat import ChatTurn
from ru_runtime.voice.gpu_lifecycle import GpuLifecycle, VoiceServiceController
from ru_runtime.generation.runner import (
    BACKEND_GGUF,
    BACKEND_KILO,
    BACKEND_OPENCODE,
)
from ru_runtime.memory.store import MemoryKind
from ru_runtime.memory.extraction import is_sensitive_or_unsafe
from ru_runtime.memory.learning import ExtractFact, extract_facts_with_llm
from ru_runtime.generation.openai import OpenAIGenerationCancelled
from ru_runtime.observation.screen_reaction import (
    ReactionCoordinator,
    ReactionRequest,
    VisionObservation,
    format_observation_prompt,
)
from ru_runtime.speech.runtime import (
    CapturedAudio,
    SpeechEngineConfig,
    SpeechRecognitionError,
    build_speech_recognizer,
)
from ru_runtime.vts.overlay import start_overlay
from ru_runtime.vts.actions import VtsActions
from ru_runtime.vts.catalog import (
    VtsCatalog,
    catalog_instruction,
    split_reply_metadata,
)
from ru_runtime.vts.config import MotionConfig
from ru_runtime.vts.manager import VTSManager
from ru_runtime.voice.pacing import effective_voice_speed, voice_pace_for
from ru_runtime.voice.runtime import (
    VOICE_SELECTIONS,
    voice_selection_by_label,
    voice_selection_from_saved,
    voice_service_config,
    validate_voice_environment,
)
from ru_runtime.voice.style import (
    ORIGINAL_STYLE_ID,
    VoiceStyleId,
    voice_style_by_id,
    voice_style_from_saved,
)

logger = logging.getLogger(__name__)

def _valid_desktop_token(candidate: str) -> bool:
    # Read the token on every call because local harnesses may populate the
    # environment after this module is imported. A module-level capture would
    # leave authentication permanently disabled in that process.
    token = os.environ.get("RU_DESKTOP_TOKEN", "").strip()
    if not token:
        # Fail closed: an unconfigured backend must not answer local callers
        # without credentials (any local process could otherwise drive the
        # model, TTS, or screen observations).
        logger.warning(
            "API auth is disabled: RU_DESKTOP_TOKEN is not set; refusing requests"
        )
        return False
    return hmac.compare_digest(candidate, token)


# ── paths / files ─────────────────────────────────────────────────────

_configured_data_root = os.environ.get("RU_DATA_ROOT", "").strip()
DATA_ROOT = (
    Path(_configured_data_root)
    if _configured_data_root
    else canonical_data_root()
)
SETTINGS_FILE = DATA_ROOT / "settings.json"
HISTORY_FILE = DATA_ROOT / "history.json"
SESSION_DIR = DATA_ROOT / "sessions"
PROJECT_ROOT = Path(__file__).resolve().parents[4]

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
    "share_memory_with_cloud": True,
    "kilo_reasoning_enabled": False,
    "checkpoint_path": "",
    "voice_label": "Ru (OmniVoice)",
    "voice_style_id": ORIGINAL_STYLE_ID,
    "voice_speed": 1.0,
    "auto_pace_enabled": False,
    "auto_read_replies": True,
    "show_subtitles": False,
}

_VALID_BACKENDS = (BACKEND_GGUF, BACKEND_OPENCODE, BACKEND_KILO)

_BACKEND_ALIASES: dict[str, str] = {
    "gguf": BACKEND_GGUF,
    "opencode": BACKEND_OPENCODE,
    "kilo": BACKEND_KILO,
}

# ── shared state ──────────────────────────────────────────────────────

_HISTORY_LOCK = threading.Lock()
_SETTINGS_LOCK = threading.Lock()
_TURN_LOCK = threading.Lock()
_VOICE_LOCK = threading.Lock()
_GPU_LOCK = threading.Lock()
_VTS_LOCK = threading.Lock()
_MEMORY_TURN_LOCK = threading.Lock()
_PENDING_REACTION_LOCK = threading.Lock()
_CLOUD_VOICE_WARMUP_LOCK = threading.Lock()

_MEMORY_QUEUE_MAXSIZE = 50
_MEMORY_LOCK_TIMEOUT_SECONDS = 10.0
_MEMORY_SHUTDOWN_TIMEOUT_SECONDS = 120.0
_MEMORY_QUEUE: queue.Queue = queue.Queue(maxsize=_MEMORY_QUEUE_MAXSIZE)
_MEMORY_QUEUE_LOCK = threading.Lock()
_MEMORY_QUEUE_GENERATION = 0
_MEMORY_SENTINEL = object()
_MEMORY_WORKER_STARTED = threading.Event()
_MEMORY_STOP = threading.Event()
_MEMORY_THREAD: threading.Thread | None = None

_SCREEN_REACTIONS = ReactionCoordinator()
_PENDING_REACTION_TEXT: dict[str, tuple[str, float]] = {}

_VTS_MANAGER: VTSManager | None = None
_VTS_THREAD: threading.Thread | None = None
_VOICE_PROCESS: subprocess.Popen | None = None
_GPU_LIFECYCLE: GpuLifecycle | None = None

core = RuRuntimeCore()

# ── helpers ───────────────────────────────────────────────────────────

@contextlib.contextmanager
def _chat_turn_locks() -> contextlib.AbstractContextManager[None]:
    """Hold both turn locks for the full duration of a chat generation.

    Observations and voice streams check these locks to decide whether a
    chat generation is active; without them a long chat would let a screen
    reaction or voice warmup run concurrently with the model.
    """
    acquired: list[threading.Lock] = []
    try:
        for lock in (_TURN_LOCK, _MEMORY_TURN_LOCK):
            lock.acquire()
            acquired.append(lock)
        yield
    finally:
        for lock in reversed(acquired):
            lock.release()

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


_SESSION_LOCK = threading.Lock()

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
    with _SETTINGS_LOCK:
        return _load_settings_unlocked()


def _load_settings_unlocked() -> dict:
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
    for key in (
        "allow_web_search",
        "enable_self_learning",
        "share_memory_with_cloud",
        "kilo_reasoning_enabled",
        "auto_pace_enabled",
        "auto_read_replies",
        "show_subtitles",
    ):
        if not isinstance(merged.get(key), bool):
            merged[key] = _DEFAULT_SETTINGS[key]
    merged["voice_style_id"] = voice_style_from_saved(
        merged.get("voice_style_id")
    ).id
    return merged


def save_settings_to_file(data: dict) -> None:
    with _SETTINGS_LOCK:
        atomic_write_json(SETTINGS_FILE, data, indent=2)


def _merge_settings(mutate: "Callable[[dict], None]") -> None:
    """Read-modify-write of the settings file under one lock so concurrent
    updates (settings API + VTS state + motion config) cannot clobber each other."""
    with _SETTINGS_LOCK:
        settings = _effective_settings_unlocked()
        mutate(settings)
        atomic_write_json(SETTINGS_FILE, settings, indent=2)


def _effective_settings_unlocked() -> dict:
    """effective_settings() without acquiring _SETTINGS_LOCK (caller must
    already hold it, or be in a lock-free read-only context)."""
    saved = _load_settings_unlocked()
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
    for key in (
        "allow_web_search",
        "enable_self_learning",
        "share_memory_with_cloud",
        "kilo_reasoning_enabled",
        "auto_pace_enabled",
        "auto_read_replies",
        "show_subtitles",
    ):
        if not isinstance(merged.get(key), bool):
            merged[key] = _DEFAULT_SETTINGS[key]
    merged["voice_style_id"] = voice_style_from_saved(
        merged.get("voice_style_id")
    ).id
    return merged


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
    with _SESSION_LOCK:
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
    with _SESSION_LOCK:
        return _load_session_file(session_id)


def _session_turns(session_id: str) -> list[ChatTurn]:
    with _SESSION_LOCK:
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
        global _VOICE_PROCESS
        if _VOICE_PROCESS is not None and _VOICE_PROCESS.poll() is None:
            deadline = time.monotonic() + VOICE_START_TIMEOUT_SECONDS
            while time.monotonic() < deadline:
                if voice_available():
                    return
                time.sleep(0.2)
            raise RuntimeError(
                "OmniVoice service did not open port 5052 in time from the existing process."
            )
        config = _voice_config()
        validate_voice_environment(config)
        pythonw = config.python_path.with_name("pythonw.exe")
        if pythonw.is_file():
            executable = str(pythonw)
        elif config.python_path.is_file():
            executable = str(config.python_path)
        else:
            raise FileNotFoundError(
                f"OmniVoice Python interpreter not found: {config.python_path}"
            )
        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        startup_info = None
        if os.name == "nt":
            startup_info = subprocess.STARTUPINFO()
            startup_info.dwFlags |= getattr(subprocess, "STARTF_USESHOWWINDOW", 0)
            startup_info.wShowWindow = subprocess.SW_HIDE
        _VOICE_PROCESS = subprocess.Popen(
            [executable, str(config.server_script)],
            cwd=str(PROJECT_ROOT),
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            creationflags=creation_flags,
            startupinfo=startup_info,
        )
        if _VOICE_PROCESS.stderr is not None:
            def _drain_voice_stderr(pipe):
                try:
                    for line in iter(pipe.readline, b""):
                        logger.debug("OmniVoice stderr: %s", line.decode("utf-8", errors="replace").rstrip())
                except Exception:
                    pass
                finally:
                    try:
                        pipe.close()
                    except Exception:
                        pass
            threading.Thread(target=_drain_voice_stderr, args=(_VOICE_PROCESS.stderr,), daemon=True).start()
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


def request_voice_audio(
    text: str,
    voice_label: str,
    speed: float,
    voice_style_id: VoiceStyleId = ORIGINAL_STYLE_ID,
) -> bytes:
    selection = voice_selection_by_label(voice_label) or VOICE_SELECTIONS[0]
    style = voice_style_by_id(voice_style_id)
    status, body = _post_voice(
        "/tts",
        {
            "text": text,
            "voice_profile": selection.profile_id,
            "speed": speed,
            "voice_style_id": style.id,
        },
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


def _should_warm_voice_for_cloud(settings: dict[str, object]) -> bool:
    return (
        core.runner.backend in (BACKEND_OPENCODE, BACKEND_KILO)
        and settings.get("auto_read_replies", True) is True
        and _voice_selection().engine_id == "omnivoice"
    )


def _schedule_cloud_voice_warmup() -> bool:
    lock_acquired = False
    try:
        if not _should_warm_voice_for_cloud(effective_settings()):
            return False
        if not _CLOUD_VOICE_WARMUP_LOCK.acquire(blocking=False):
            return False
        lock_acquired = True
        selection = _voice_selection()

        def warm() -> None:
            try:
                _gpu_lifecycle().warm_voice_for_cloud(selection, logger.info)
            except Exception:
                logger.exception("Background cloud voice warm-up failed")
            finally:
                _CLOUD_VOICE_WARMUP_LOCK.release()

        threading.Thread(target=warm, name="cloud-voice-warmup", daemon=True).start()
    except Exception:
        if lock_acquired:
            _CLOUD_VOICE_WARMUP_LOCK.release()
        logger.exception("Failed to schedule background cloud voice warm-up")
        return False
    return True


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
        kilo_reasoning_enabled=effective_settings()["kilo_reasoning_enabled"],
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
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            logger.warning("Voice process did not exit after SIGKILL; abandoning handle")
            return
    if process.returncode != 0:
        stderr = process.stderr
        if stderr is not None:
            try:
                err_text = stderr.read(_MAX_ERROR_BODY_BYTES).decode("utf-8", errors="replace")
            except Exception:
                err_text = ""
        else:
            err_text = ""
        logger.warning("Voice process exited with code %s: %s", process.returncode, err_text.strip())


def _shutdown_runtime() -> None:
    _MEMORY_STOP.set()
    _MEMORY_WORKER_STARTED.clear()
    global _MEMORY_THREAD
    _invalidate_memory_queue()
    try:
        _MEMORY_QUEUE.put_nowait(_MEMORY_SENTINEL)
    except queue.Full:
        pass
    memory_thread = _MEMORY_THREAD
    if memory_thread is not None and memory_thread.is_alive():
        try:
            memory_thread.join(timeout=_MEMORY_SHUTDOWN_TIMEOUT_SECONDS)
        except Exception as error:
            logger.warning("Memory learner thread join failed: %s", error)
    if memory_thread is not None and memory_thread.is_alive():
        logger.error(
            "Memory learner did not stop before runtime shutdown; leaving core open to avoid a use-after-close"
        )
    else:
        _MEMORY_THREAD = None
    manager = _VTS_MANAGER
    if manager is not None and callable(getattr(manager, "shutdown", None)):
        manager.shutdown()
    _stop_owned_voice_service()
    if memory_thread is None or not memory_thread.is_alive():
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
                _restore_vts_gui_state(manager)
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
    _merge_settings(lambda settings: settings.update({"vts_motion": config.to_dict()}))


def _load_vts_gui_state() -> dict:
    default = {
        "overlay_enabled": False,
        "mouse_look_enabled": False,
        "mouse_look_sensitivity": 1.0,
    }
    stored = load_settings().get("vts_gui_state") or {}
    try:
        sensitivity = float(stored.get("mouse_look_sensitivity", 1.0))
        sensitivity = 0.1 if sensitivity < 0.1 else (3.0 if sensitivity > 3.0 else sensitivity)
        return {
            "overlay_enabled": bool(stored.get("overlay_enabled", False)),
            "mouse_look_enabled": bool(stored.get("mouse_look_enabled", False)),
            "mouse_look_sensitivity": sensitivity,
        }
    except (TypeError, ValueError):
        return dict(default)


def _save_vts_gui_state(state: dict) -> None:
    try:
        sensitivity = float(state.get("mouse_look_sensitivity", 1.0))
    except (TypeError, ValueError):
        sensitivity = 1.0
    sensitivity = 0.1 if sensitivity < 0.1 else (3.0 if sensitivity > 3.0 else sensitivity)

    def _mutate(settings: dict) -> None:
        settings["vts_gui_state"] = {
            "overlay_enabled": bool(state.get("overlay_enabled", False)),
            "mouse_look_enabled": bool(state.get("mouse_look_enabled", False)),
            "mouse_look_sensitivity": sensitivity,
        }

    _merge_settings(_mutate)


def _restore_vts_gui_state(manager) -> None:
    state = _load_vts_gui_state()
    if state["overlay_enabled"]:
        start_overlay()
    if state["mouse_look_enabled"]:
        manager.set_mouse_tracking(True, state["mouse_look_sensitivity"])


# ── memory / self-learning ────────────────────────────────────────────

def _memory_turn_generation_is_current(generation: int) -> bool:
    with _MEMORY_QUEUE_LOCK:
        return generation == _MEMORY_QUEUE_GENERATION


def _invalidate_memory_queue() -> None:
    global _MEMORY_QUEUE_GENERATION
    with _MEMORY_QUEUE_LOCK:
        _MEMORY_QUEUE_GENERATION += 1
    while True:
        try:
            _MEMORY_QUEUE.get_nowait()
        except queue.Empty:
            return
        else:
            try:
                _MEMORY_QUEUE.task_done()
            except ValueError:
                pass


def _learn_from_turn(prompt: str, reply: str) -> None:
    if not effective_settings()["enable_self_learning"]:
        return
    with _MEMORY_QUEUE_LOCK:
        generation = _MEMORY_QUEUE_GENERATION
    item = (generation, prompt, reply)
    try:
        _MEMORY_QUEUE.put_nowait(item)
    except queue.Full:
        try:
            _MEMORY_QUEUE.get_nowait()
        except queue.Empty:
            pass
        else:
            try:
                _MEMORY_QUEUE.task_done()
            except ValueError:
                pass
        try:
            _MEMORY_QUEUE.put_nowait(item)
        except queue.Full:
            logger.debug("Memory queue full; dropping learning turn")


def _process_memory_turn(prompt: str, reply: str, generation: int | None = None) -> None:
    if generation is not None and not _memory_turn_generation_is_current(generation):
        logger.debug("Skipping stale self-learning turn after memory clear")
        return
    settings = effective_settings()
    hosted = core.runner.backend in (BACKEND_OPENCODE, BACKEND_KILO)
    if hosted and not settings.get("share_memory_with_cloud", _DEFAULT_SETTINGS["share_memory_with_cloud"]):
        logger.debug("Skipping hosted self-learning because cloud-memory sharing is disabled")
        return
    if is_sensitive_or_unsafe(prompt) or is_sensitive_or_unsafe(reply):
        logger.debug("Bỏ qua self-learning vì lượt trò chuyện nhạy cảm")
        return
    if not _MEMORY_TURN_LOCK.acquire(blocking=False):
        logger.debug("Model busy; skipping self-learning turn")
        return
    try:
        if generation is not None and not _memory_turn_generation_is_current(generation):
            return
        generation_context = (
            _gpu_lifecycle().local_generation()
            if core.runner.backend == BACKEND_GGUF
            else nullcontext()
        )
        with generation_context:
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
            item = _MEMORY_QUEUE.get(timeout=0.5)
        except queue.Empty:
            continue
        try:
            if item is _MEMORY_SENTINEL:
                return
            generation, prompt, reply = item
            _process_memory_turn(prompt, reply, generation)
        except Exception as error:
            logger.warning("Self-learning worker error: %s", error)
        finally:
            try:
                _MEMORY_QUEUE.task_done()
            except ValueError:
                pass


def _start_memory_learner() -> None:
    global _MEMORY_THREAD
    if _MEMORY_WORKER_STARTED.is_set() and _MEMORY_THREAD is not None and _MEMORY_THREAD.is_alive():
        return
    _MEMORY_STOP.clear()
    _MEMORY_WORKER_STARTED.set()
    _MEMORY_THREAD = threading.Thread(target=_memory_learner_loop, name="memory-learner", daemon=True)
    _MEMORY_THREAD.start()


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
    _schedule_cloud_voice_warmup()
    try:
        yield
    finally:
        from ru_runtime.generation.search import shutdown_search_executor
        shutdown_search_executor()
        _shutdown_runtime()
