from __future__ import annotations

import logging
import re
import queue
import socket
import subprocess
import sys

import threading
import traceback
import tkinter as tk
import customtkinter as ctk
import csv
from time import monotonic, perf_counter, sleep
from pathlib import Path
from tkinter import filedialog, messagebox
from typing import Any

import os
import json
import speech_recognition as sr
from ._atomic_write import atomic_write_json
from .vts_manager import VTSManager
from .vts_actions import VTS_ACTION_INSTRUCTION, requested_vts_actions
from .vts_catalog import VtsCatalog, catalog_instruction, parse_catalog_reply
import pygame

from .chat import ChatTurn
from .conversation_store import ConversationStore
from .llama3_1_runtime import (
    clear_llama_generation_cache,
    dependency_status,
    generate_llama_response_hf,
    is_llama_generation_cached,
)
from .llama_cpp_runtime import LlamaCppRuntime
from .gui_presentation import (
    append_stream_text,
    conversation_display_segments,
    format_generation_status,
    safe_streamed_response,
)
from .ru_style import RU_CLOUD_SYSTEM_PROMPT, RU_SYSTEM_PROMPT
from .memory import MemoryStore, StructuredMemoryStore
from .search import perform_web_search
from .reply_orchestration import execute_reply
from .gpu_lifecycle import GpuLifecycle, VoiceServiceController
from .speech_runtime import (
    SPEECH_ENGINE_GOOGLE,
    SPEECH_ENGINE_LABELS,
    CapturedAudio,
    SpeechEngineConfig,
    SpeechRecognitionError,
    SpeechRecognizer,
    build_speech_recognizer,
    speech_settings_for_persistence,
    speech_settings_from_mapping,
)
from .tts_chunks import split_tts_chunks
from .voice_runtime import (
    DEFAULT_VOICE_SELECTION,
    VOICE_SELECTIONS,
    VoiceSelection,
    voice_selection_from_saved,
    voice_service_config,
)
from .voice_pacing import (
    DEFAULT_VOICE_SPEED,
    VOICE_PACE_INSTRUCTION,
    effective_voice_speed,
    parse_voice_pace,
    voice_speed_from_saved,
)

logger = logging.getLogger(__name__)

PROJECT_ROOT = Path(__file__).resolve().parents[2]
MODEL_NAME = "Ru"
GUI_SETTINGS_PATH = PROJECT_ROOT / "data" / "gui_settings.json"
DEFAULT_MAX_NEW_TOKENS = "1024"
DEFAULT_ALLOW_WEB_SEARCH = True
TTS_PREFETCH_WAVS = 2
OVERLAY_EXE_PATH = PROJECT_ROOT / "tools" / "ru_desktop_overlay" / "out" / "build" / "Release" / "RuDesktopOverlay.exe"
OVERLAY_SENDER = "VTubeStudioSpout"
BACKEND_TRANSFORMERS = "transformers"
BACKEND_GGUF = "gguf"
BACKEND_OPENCODE = "opencode"
BACKEND_LABELS = {
    "GGUF (default)": BACKEND_GGUF,
    "Transformers (fallback)": BACKEND_TRANSFORMERS,
    "OpenCode Zen API": BACKEND_OPENCODE,
}

from .openai_runtime import OPENCODE_BASE_URL, OpenAIRuntime

# ── Design System ──────────────────────────────────────────────────────────
# Dark chat theme — deep, warm-neutral background, purple accents, green CTA.
_DESIGN = {
    "sidebar_bg": "#111118",
    "sidebar_hover": "#1e1e2e",
    "sidebar_text": "#a0a0b8",
    "chat_bg": "#16161f",
    "surface": "#1c1c2a",
    "surface_hover": "#262640",
    "bubble_user_bg": "#2d2b55",
    "bubble_user_text": "#e8e8f0",
    "bubble_ru_bg": "#1e1e30",
    "bubble_ru_text": "#d8d8e8",
    "composer_bg": "#24243a",
    "composer_border": "#2e2e4a",
    "header_bg": "#1a1a28",
    "accent": "#7c3aed",
    "accent_hover": "#6d28d9",
    "accent_subtle": "#2d2b55",
    "green": "#10b981",
    "green_hover": "#059669",
    "text_primary": "#e2e2f0",
    "text_muted": "#6b6b80",
    "text_label": "#8888a0",
    "border": "#2e2e45",
    "corner": 12,
    "bubble_corner": 14,
    "sidebar_corner": 10,
    "composer_corner": 16,
}

# ── Shared resource: status indicator dot string ─────────────────────────
_STATUS_DOT = "●"


def _check_cuda() -> bool:
    """Lazy CUDA availability check; avoids importing torch at module load."""
    try:
        import torch  # noqa: F811
        return torch.cuda.is_available()
    except ImportError:
        return False


def clean_tts_text(text: str) -> str:
    clean_text = re.sub(r"\[SEARCH:.*?\]", "", text).strip()
    clean_text = re.sub(r"<thought>.*?</thought>", "", clean_text, flags=re.DOTALL)
    clean_text = re.sub(r"<think>.*?</think>", "", clean_text, flags=re.DOTALL)
    clean_text = re.sub(r"\s*\[\[VTS:[^\]]*\]\]", "", clean_text, flags=re.IGNORECASE)
    clean_text = re.sub(r"\s*\[\[VOICE_PACE:[^\]]*\]\]", "", clean_text, flags=re.IGNORECASE)
    return re.sub(r"\s+", " ", clean_text.replace("*", "")).strip()


def voice_service_available(selection: VoiceSelection) -> bool:
    config = voice_service_config(selection, PROJECT_ROOT)
    try:
        with socket.create_connection((config.host, config.port), timeout=0.2):
            return True
    except OSError:
        return False


def overlay_process_ids(*, run=subprocess.run) -> tuple[int, ...]:
    """Return running RuDesktopOverlay process IDs on Windows."""

    try:
        result = run(
            ["tasklist", "/FI", "IMAGENAME eq RuDesktopOverlay.exe", "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
            check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except OSError:
        return ()

    process_ids: list[int] = []
    for row in csv.reader(result.stdout.splitlines()):
        if len(row) < 2 or row[0].strip().casefold() != "rudesktopoverlay.exe":
            continue
        try:
            process_ids.append(int(row[1].strip()))
        except ValueError:
            continue
    return tuple(process_ids)


def ensure_voice_service(
    selection: VoiceSelection, *, process_factory=None, sleep=sleep, monotonic=monotonic
) -> None:
    config = voice_service_config(selection, PROJECT_ROOT)
    if voice_service_available(selection):
        return
    if not config.python_path.is_file() or not config.server_script.is_file():
        raise RuntimeError(
            f"{selection.engine_id} runtime is missing. Reinstall the local voice environment."
        )

    process_factory = process_factory or subprocess.Popen
    creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) | getattr(subprocess, "DETACHED_PROCESS", 0)
    process_factory(
        [str(config.python_path), str(config.server_script)],
        cwd=str(PROJECT_ROOT),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=creation_flags,
    )

    deadline = monotonic() + 12
    while monotonic() < deadline:
        if voice_service_available(selection):
            return
        sleep(0.2)
    raise RuntimeError(
        f"{selection.engine_id} voice service did not start. Check the local voice environment."
    )


def request_tts_audio(
    text: str,
    selection: VoiceSelection,
    *,
    speed: float = DEFAULT_VOICE_SPEED,
    post=None,
) -> str:
    config = voice_service_config(selection, PROJECT_ROOT)
    if post is None:
        import requests

        post = requests.post
    try:
        response = post(
            f"{config.base_url}/tts",
            json={"text": text, "voice_profile": selection.profile_id, "speed": speed},
            timeout=90,
        )
    except Exception as error:
        raise RuntimeError("Voice server is offline. Start OmniVoice and try again.") from error
    if response.status_code != 200:
        try:
            body = response.json()
        except Exception:
            body = {}
        detail = body.get("error")
        suffix = f": {detail}" if isinstance(detail, str) and detail else ""
        raise RuntimeError(f"Voice server failed (HTTP {response.status_code}){suffix}")
    try:
        body = response.json()
        audio_file = body.get("audio_path")
    except Exception as error:
        raise RuntimeError("Voice server returned an invalid response.") from error
    if not isinstance(audio_file, str) or not Path(audio_file).is_file():
        raise RuntimeError("Voice server did not return a playable audio file.")
    return audio_file


def request_voice_control(
    selection: VoiceSelection, path: str, payload: dict[str, str] | None = None, *, post=None
) -> dict[str, bool]:
    config = voice_service_config(selection, PROJECT_ROOT)
    if post is None:
        import requests

        post = requests.post
    try:
        response = post(f"{config.base_url}{path}", json=payload or {}, timeout=30)
    except Exception as error:
        raise RuntimeError("Voice server is offline. Start OmniVoice and try again.") from error
    if response.status_code != 200:
        raise RuntimeError("Voice service rejected lifecycle request.")
    try:
        payload = response.json()
    except Exception as error:
        raise RuntimeError("Voice service returned an invalid lifecycle response.") from error
    if not isinstance(payload, dict):
        raise RuntimeError("Voice service returned an invalid lifecycle response.")
    return payload


def play_wav_blocking(audio_file: str) -> None:
    sound = pygame.mixer.Sound(audio_file)
    sound.play()
    pygame.time.wait(int(sound.get_length() * 1000))


def default_paths() -> Path:
    candidates = (
        PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v6.2-fresh",
        PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v6.1",
        PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v6",
    )
    return next((path for path in candidates if path.exists()), candidates[-1])


def parse_generation_settings(max_new_tokens: str, temperature: str, top_k: str) -> tuple[int, float, int]:
    max_tokens = int(max_new_tokens)
    temp = float(temperature)
    top = int(top_k)
    if max_tokens <= 0:
        raise ValueError("max new tokens must be positive")
    if temp <= 0:
        raise ValueError("temperature must be positive")
    if top < 0:
        raise ValueError("top-k cannot be negative")
    return max_tokens, temp, top


class ModelRunner:
    def __init__(self) -> None:
        self.checkpoint_path: Path | None = None
        self.base_model_path: Path = PROJECT_ROOT / "models" / "llama3.1-8b-instruct-hf"
        self.backend = BACKEND_GGUF
        self.gguf_runtime = LlamaCppRuntime(project_root=PROJECT_ROOT)
        self.openai_runtime: OpenAIRuntime | None = None

    def select_backend(self, backend: str) -> None:
        if backend not in (BACKEND_TRANSFORMERS, BACKEND_GGUF, BACKEND_OPENCODE):
            raise ValueError(f"unsupported backend: {backend}")
        self.backend = backend

    def load(
        self,
        checkpoint_path: str | Path,
        api_key: str = "",
        model_name: str = "deepseek-v4-flash-free",
    ) -> str:
        if self.backend == BACKEND_OPENCODE:
            self.openai_runtime = OpenAIRuntime(
                api_key,
                base_url=OPENCODE_BASE_URL,
                model_name=model_name,
            )
            return "Ready (OpenCode Zen API)"
        if self.backend == BACKEND_GGUF:
            self.gguf_runtime.validate_assets()
            self.checkpoint_path = Path(checkpoint_path)
            return f"Ready (llama.cpp GGUF experiment) | Ru {self.checkpoint_path.name.split('-')[-1]} LoRA"
        missing_dependencies = dependency_status()
        if missing_dependencies:
            packages = ", ".join(missing_dependencies)
            raise RuntimeError(
                f"Missing Ru runtime dependencies: {packages}. "
                "Install the runtime extra before starting the model."
            )
        adapter_dir = Path(checkpoint_path)
        if not adapter_dir.is_dir():
            raise FileNotFoundError(f"adapter directory not found: {adapter_dir}")
        self.checkpoint_path = adapter_dir
        return f"Ready (Llama 3.1 8B QLoRA) | {adapter_dir.name}"

    def is_cached(self, checkpoint_path: str | Path) -> bool:
        if self.backend == BACKEND_OPENCODE:
            return True
        if self.backend == BACKEND_GGUF:
            return self.gguf_runtime.is_cached()
        return is_llama_generation_cached(self.base_model_path, checkpoint_path)

    def release_gpu(self) -> None:
        clear_llama_generation_cache()
        self.gguf_runtime.stop()

    def _generate(
        self,
        messages: tuple[tuple[str, str], ...],
        max_new_tokens: int,
        temperature: float,
        top_k: int,
        *,
        on_stream_delta=None,
        on_complete_timing=None,
    ) -> str:
        if self.backend == BACKEND_OPENCODE and self.openai_runtime:
            kwargs = {
                "on_complete_timing": on_complete_timing,
            }
            if on_stream_delta is not None:
                kwargs["on_delta"] = on_stream_delta
            return self.openai_runtime.generate(
                messages,
                max_new_tokens,
                temperature,
                top_k,
                **kwargs,
            )
        if self.backend == BACKEND_GGUF:
            return self.gguf_runtime.generate(messages, max_new_tokens, temperature, top_k)
        return generate_llama_response_hf(
            str(self.base_model_path),
            messages,
            self.checkpoint_path,
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
            do_sample=True,
        )

    def generate_reply(
        self,
        prompt: str,
        max_new_tokens: int,
        temperature: float,
        top_k: int,
        turns_history: list[ChatTurn] | None = None,
        memory_context: str | None = None,
        allow_web_search: bool = False,
        vts_instruction: str = "",
        *,
        on_stream_delta=None,
        on_stream_reset=None,
        on_complete_timing=None,
    ) -> str:
        system_prompt = RU_CLOUD_SYSTEM_PROMPT if self.backend == BACKEND_OPENCODE else RU_SYSTEM_PROMPT
        return execute_reply(
            self._generate,
            prompt=prompt,
            system_instruction=system_prompt + VTS_ACTION_INSTRUCTION + VOICE_PACE_INSTRUCTION + vts_instruction,
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
            turns_history=turns_history or (),
            memory_context=memory_context,
            allow_web_search=allow_web_search,
            web_search=perform_web_search,
            on_stream_delta=on_stream_delta,
            on_stream_reset=on_stream_reset,
            on_complete_timing=on_complete_timing,
        )


class RuApp:
    def __init__(self, root: ctk.CTk) -> None:
        self.root = root
        self.runner = ModelRunner()
        self.worker: threading.Thread | None = None
        self.gpu_transition_lock = threading.RLock()
        self.gpu_lifecycle = GpuLifecycle(
            self.gpu_transition_lock,
            release_local_runtime=self.runner.release_gpu,
            voice_services={
                DEFAULT_VOICE_SELECTION.engine_id: VoiceServiceController(
                    available=lambda: voice_service_available(DEFAULT_VOICE_SELECTION),
                    ensure=lambda: ensure_voice_service(DEFAULT_VOICE_SELECTION),
                    request=lambda path, payload=None: request_voice_control(
                        DEFAULT_VOICE_SELECTION, path, payload
                    ),
                )
            },
        )
        self._speech_generation = 0
        self._stream_response_anchor: str | None = None
        self._stream_raw_response = ""
        self.session_turns: list[ChatTurn] = []
        self.last_turn: ChatTurn | None = None
        self.last_turn_saved = False
        self.voice_mode_active = False
        self.conversation_store = ConversationStore(
            PROJECT_ROOT / "data" / "conversations" / "approved.jsonl"
        )
        self.memory_index_path = PROJECT_ROOT / "data" / "conversations" / "memory_index.faiss"
        self.memory_texts_path = PROJECT_ROOT / "data" / "conversations" / "memory_texts.json"
        self.memory_db_path = PROJECT_ROOT / "data" / "conversations" / "memory.db"
        self.memory_store: StructuredMemoryStore | None = None
        checkpoint = default_paths()
        self.checkpoint_var = ctk.StringVar(value=str(checkpoint))
        
        saved_settings = {}
        if GUI_SETTINGS_PATH.is_file():
            try:
                with open(GUI_SETTINGS_PATH, "r", encoding="utf-8") as f:
                    saved_settings = json.load(f)
            except Exception as exc:
                logger.debug("Failed to load GUI settings: %s", exc)
                
        saved_backend = saved_settings.get("backend", "GGUF (default)")
        if saved_backend == "OpenAI API":
            saved_backend = "OpenCode Zen API"
        if saved_backend not in BACKEND_LABELS:
            saved_backend = "GGUF (default)"
        self.api_key_var = ctk.StringVar(value=saved_settings.get("api_key", ""))
        self.api_model_var = ctk.StringVar(
            value=saved_settings.get("api_model", "deepseek-v4-flash-free")
        )
        self.backend_var = ctk.StringVar(value=saved_backend)
        saved_selection = voice_selection_from_saved(saved_settings.get("voice_profile"))
        self.voice_selection_var = ctk.StringVar(value=saved_selection.label)
        self.voice_speed_var = ctk.DoubleVar(value=voice_speed_from_saved(saved_settings.get("voice_speed")))
        self.auto_voice_pace_var = ctk.BooleanVar(value=bool(saved_settings.get("auto_voice_pace", True)))
        saved_speech_config = speech_settings_from_mapping(saved_settings)
        saved_speech_label = next(
            (
                label
                for label, engine_id in SPEECH_ENGINE_LABELS.items()
                if engine_id == saved_speech_config.engine_id
            ),
            next(iter(SPEECH_ENGINE_LABELS)),
        )
        self.speech_engine_var = ctk.StringVar(value=saved_speech_label)

        def _save_settings(*args) -> None:
            settings = {
                "api_key": self.api_key_var.get(),
                "api_model": self.api_model_var.get(),
                "backend": self.backend_var.get(),
                "voice_profile": self._selected_voice_selection().label,
                "voice_speed": round(self.voice_speed_var.get(), 2),
                "auto_voice_pace": self.auto_voice_pace_var.get(),
            }
            settings.update(speech_settings_for_persistence(self._speech_config()))
            try:
                atomic_write_json(GUI_SETTINGS_PATH, settings)
            except Exception as exc:
                logger.debug("Failed to save GUI settings: %s", exc)

        self._save_gui_settings = _save_settings
        self.api_key_var.trace_add("write", _save_settings)
        self.api_model_var.trace_add("write", _save_settings)
        self.backend_var.trace_add("write", _save_settings)
        self.voice_selection_var.trace_add("write", _save_settings)
        self.voice_speed_var.trace_add("write", _save_settings)
        self.auto_voice_pace_var.trace_add("write", _save_settings)
        self.speech_engine_var.trace_add("write", self._on_speech_settings_changed)
        _save_settings()
        
        self.max_tokens_var = ctk.StringVar(value=DEFAULT_MAX_NEW_TOKENS)
        self.temperature_var = ctk.StringVar(value="0.7")
        self.top_k_var = ctk.StringVar(value="20")
        _cuda_available = _check_cuda()
        self.status_var = ctk.StringVar(
            value=f"CUDA available: {'yes' if _cuda_available else 'no'} | Ready"
        )
        self.api_models_list: list[str] = [self.api_model_var.get()]
        self.tts_enabled_var = ctk.BooleanVar(value=True)
        self._mouse_look_enabled = False
        self.remember_conversations_var = ctk.BooleanVar(value=True)
        self.allow_web_search_var = ctk.BooleanVar(value=DEFAULT_ALLOW_WEB_SEARCH)
        pygame.mixer.init()
        self.vts_manager = VTSManager()
        threading.Thread(target=self.vts_manager.run, daemon=True).start()
        self.is_recording = False
        self.microphone_recognizer = sr.Recognizer()
        self._speech_runtime: SpeechRecognizer | None = None
        self._speech_runtime_config: SpeechEngineConfig | None = None
        self.generate_button: ctk.CTkButton
        self.new_chat_button: ctk.CTkButton
        self.mark_button: ctk.CTkButton
        self.settings_button: ctk.CTkButton
        self.overlay_button: ctk.CTkButton
        self.prompt_text: ctk.CTkTextbox
        self.transcript_text: ctk.CTkTextbox
        self._has_empty_state = True
        self._build()

    def _build(self) -> None:
        D = _DESIGN
        self.root.title(MODEL_NAME)
        self.root.geometry("1100x750")
        self.root.minsize(800, 600)

        # Grid layout: 2 columns (Sidebar, Main Chat)
        self.root.grid_columnconfigure(1, weight=1)
        self.root.grid_columnconfigure(0, weight=0)
        self.root.grid_rowconfigure(0, weight=1)

        # ── SIDEBAR (Left) ─────────────────────────────────────────────
        sidebar = ctk.CTkFrame(self.root, width=220, corner_radius=0, fg_color=D["sidebar_bg"])
        sidebar.grid(row=0, column=0, sticky="nsew")
        sidebar.grid_rowconfigure(6, weight=1)

        # Logo header
        logo_frame = ctk.CTkFrame(sidebar, fg_color="transparent")
        logo_frame.grid(row=0, column=0, padx=18, pady=(24, 20), sticky="ew")

        ctk.CTkLabel(
            logo_frame, text="Ru",
            font=ctk.CTkFont(size=26, weight="bold"),
            text_color=D["text_primary"],
        ).pack(side="left")

        ctk.CTkLabel(
            logo_frame, text="Local Llama",
            font=ctk.CTkFont(size=12),
            text_color=D["text_muted"],
        ).pack(side="left", padx=(10, 0), pady=(6, 0))

        # — Nav buttons —
        btn_kw = dict(anchor="w", height=38, corner_radius=D["sidebar_corner"])

        self.new_chat_button = ctk.CTkButton(
            sidebar, text="＋  New Chat",
            fg_color=D["accent_subtle"], hover_color=D["surface_hover"],
            command=self._new_chat, **btn_kw,
        )
        self.new_chat_button.grid(row=1, column=0, padx=14, pady=(0, 6), sticky="ew")

        self.settings_button = ctk.CTkButton(
            sidebar, text="⚙  Settings",
            fg_color="transparent", hover_color=D["sidebar_hover"],
            command=self._open_settings, **btn_kw,
        )
        self.settings_button.grid(row=2, column=0, padx=14, pady=2, sticky="ew")

        self.call_ru_button = ctk.CTkButton(
            sidebar, text="🎙  Call Ru (Voice)",
            fg_color="transparent", hover_color=D["sidebar_hover"],
            command=self._toggle_voice_mode, **btn_kw,
        )
        self.call_ru_button.grid(row=3, column=0, padx=14, pady=2, sticky="ew")

        self.overlay_button = ctk.CTkButton(
            sidebar, text="🖥  Overlay",
            fg_color="transparent", hover_color=D["sidebar_hover"],
            command=self._toggle_overlay, **btn_kw,
        )
        self.overlay_button.grid(row=4, column=0, padx=14, pady=2, sticky="ew")

        self.mouse_look_button = ctk.CTkButton(
            sidebar, text="🖱  Mouse Look",
            fg_color="transparent", hover_color=D["sidebar_hover"],
            command=self._toggle_mouse_look, **btn_kw,
        )
        self.mouse_look_button.grid(row=5, column=0, padx=14, pady=2, sticky="ew")

        # — Bottom status area —
        bottom_frame = ctk.CTkFrame(sidebar, fg_color="transparent")
        bottom_frame.grid(row=7, column=0, padx=14, pady=(0, 18), sticky="ew")

        self.tts_checkbox = ctk.CTkCheckBox(
            bottom_frame, text="Voice output",
            variable=self.tts_enabled_var,
            fg_color=D["accent_subtle"], hover_color=D["surface_hover"],
            text_color=D["text_label"],
            font=ctk.CTkFont(size=12),
        )
        self.tts_checkbox.pack(anchor="w", pady=(0, 12))

        status_card = ctk.CTkFrame(bottom_frame, fg_color=D["surface"], corner_radius=8)
        status_card.pack(fill="x")

        dot = ctk.CTkLabel(
            status_card, text=_STATUS_DOT,
            text_color=D["green"], font=ctk.CTkFont(size=10),
        )
        dot.pack(side="left", padx=(10, 4), pady=10)

        ctk.CTkLabel(
            status_card, textvariable=self.status_var,
            font=ctk.CTkFont(size=11), text_color=D["text_muted"],
        ).pack(side="left", padx=(0, 10), pady=10)

        # ── MAIN CHAT AREA (Right) ────────────────────────────────────
        main_chat = ctk.CTkFrame(self.root, corner_radius=0, fg_color=D["chat_bg"])
        main_chat.grid(row=0, column=1, sticky="nsew")
        main_chat.grid_columnconfigure(0, weight=1)
        main_chat.grid_rowconfigure(1, weight=1)

        # — Chat Header —
        header = ctk.CTkFrame(main_chat, fg_color=D["header_bg"], corner_radius=0, height=48)
        header.grid(row=0, column=0, sticky="ew")
        header.grid_propagate(False)

        ctk.CTkLabel(
            header, text="💬  Ru · " + self.backend_var.get().upper(),
            font=ctk.CTkFont(size=13), text_color=D["text_label"],
        ).pack(side="left", padx=(24, 0), pady=12)

        # — Transcript —
        self.transcript_text = ctk.CTkTextbox(
            main_chat, wrap="word",
            fg_color="transparent",
            font=ctk.CTkFont(size=14, family="Segoe UI"),
            border_spacing=0,
        )
        self.transcript_text.grid(row=1, column=0, padx=32, pady=(20, 12), sticky="nsew")

        # Bubble tag styles
        self.transcript_text.tag_config("ru_label",
            foreground=D["text_muted"],
            spacing1=16, spacing3=4,
            lmargin1=4, lmargin2=4,
        )
        self.transcript_text.tag_config("ru_bubble",
            foreground=D["bubble_ru_text"],
            background=D["bubble_ru_bg"],
            lmargin1=12, lmargin2=12, rmargin=96,
            spacing1=6, spacing2=4, spacing3=8,
        )
        self.transcript_text.tag_config("user_label",
            foreground=D["text_muted"],
            spacing1=16, spacing3=4,
            justify="right",
            rmargin=4,
        )
        self.transcript_text.tag_config("user_bubble",
            foreground=D["bubble_user_text"],
            background=D["bubble_user_bg"],
            lmargin1=96, lmargin2=96, rmargin=12,
            spacing1=6, spacing2=4, spacing3=8,
            justify="right",
        )
        self.transcript_text.tag_config("empty",
            justify="center",
            foreground=D["text_muted"],
            spacing1=120, spacing2=8, spacing3=8,
        )
        self.transcript_text.configure(state="disabled")
        self._show_empty_state()

        # — Composer —
        composer_outer = ctk.CTkFrame(main_chat, fg_color="transparent")
        composer_outer.grid(row=2, column=0, padx=32, pady=(0, 24), sticky="ew")
        composer_outer.grid_columnconfigure(0, weight=1)

        composer = ctk.CTkFrame(composer_outer, fg_color=D["composer_bg"], corner_radius=D["composer_corner"])
        composer.grid(row=0, column=0, sticky="ew")
        composer.grid_columnconfigure(0, weight=1)

        self.prompt_text = ctk.CTkTextbox(
            composer, height=52, wrap="word",
            fg_color="transparent",
            font=ctk.CTkFont(size=13),
            border_spacing=0,
        )
        self.prompt_text.grid(row=0, column=0, padx=(16, 8), pady=(8, 8), sticky="ew")
        self.prompt_text.bind("<Control-Return>", self._submit_from_keyboard)

        actions = ctk.CTkFrame(composer, fg_color="transparent")
        actions.grid(row=0, column=1, padx=(0, 8), pady=8, sticky="e")

        self.speak_button = ctk.CTkButton(
            actions, text="🎤", width=36, height=36,
            corner_radius=18,
            fg_color="transparent", hover_color=D["accent_subtle"],
            command=self._toggle_recording,
        )
        self.speak_button.pack(side="left", padx=(0, 6))

        self.generate_button = ctk.CTkButton(
            actions, text="➤", width=36, height=36,
            corner_radius=18,
            fg_color=D["green"], hover_color=D["green_hover"],
            font=ctk.CTkFont(size=16, weight="bold"),
            command=self._start_generation,
        )
        self.generate_button.pack(side="left")

        # Hidden — needed for the mark-last-response logic
        self.mark_button = ctk.CTkButton(
            self.root, text="Save useful reply",
            command=self._mark_last_response, state="disabled",
        )

        self._configure_styles()

    def _configure_styles(self) -> None:
        self.root.configure(bg=_DESIGN["chat_bg"])

    def _show_empty_state(self) -> None:
        self.transcript_text.configure(state="normal")
        self.transcript_text.insert(tk.END, "\n\n", "empty")
        self.transcript_text.insert(tk.END, "✦  Chào anh, em ở đây  ✦\n", "empty")
        self.transcript_text.insert(tk.END, "Hôm nay anh muốn kể em nghe chuyện gì?", "empty")
        self.transcript_text.configure(state="disabled")

    def _toggle_overlay(self) -> None:
        process_ids = overlay_process_ids()
        if process_ids:
            for process_id in process_ids:
                subprocess.run(
                    ["taskkill", "/PID", str(process_id), "/T", "/F"],
                    capture_output=True,
                    check=False,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                )
            self.overlay_button.configure(text="Open overlay")
            self.status_var.set("Overlay closed")
            return

        if not OVERLAY_EXE_PATH.is_file():
            self.status_var.set("Overlay unavailable: build RuDesktopOverlay first")
            return
        try:
            subprocess.Popen(
                [str(OVERLAY_EXE_PATH), "--sender", OVERLAY_SENDER],
                cwd=str(PROJECT_ROOT),
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except OSError as error:
            self.status_var.set(f"Overlay unavailable: {error}")
            return
        self.overlay_button.configure(text="Close overlay")
        self.status_var.set("Overlay opening...")

    def _toggle_mouse_look(self) -> None:
        self._mouse_look_enabled = not self._mouse_look_enabled
        self.vts_manager.set_mouse_tracking(self._mouse_look_enabled)
        label = "🖱  Mouse Look · ON" if self._mouse_look_enabled else "🖱  Mouse Look"
        fg = _DESIGN["accent_subtle"] if self._mouse_look_enabled else "transparent"
        self.mouse_look_button.configure(text=label, fg_color=fg)
        self.status_var.set(
            "Mouse look enabled — model follows cursor"
            if self._mouse_look_enabled
            else "Mouse look disabled"
        )

    def _open_settings(self) -> None:
        dialog = ctk.CTkToplevel(self.root)
        dialog.title("Settings")
        dialog.geometry("750x700")
        dialog.resizable(False, False)
        dialog.grab_set()

        content = ctk.CTkScrollableFrame(dialog, fg_color="transparent")
        content.pack(fill="both", expand=True, padx=20, pady=20)
        
        def create_section(parent, title):
            frame = ctk.CTkFrame(parent, fg_color=_DESIGN["surface"], corner_radius=10)
            frame.pack(fill="x", pady=10)
            ctk.CTkLabel(frame, text=title, font=ctk.CTkFont(weight="bold", size=14)).pack(anchor="w", padx=15, pady=(10, 5))
            inner = ctk.CTkFrame(frame, fg_color="transparent")
            inner.pack(fill="x", padx=15, pady=(0, 10))
            return inner

        model = create_section(content, "Model & Backend")
        model.columnconfigure(1, weight=1)
        self._path_row(model, 0, "Checkpoint / adapter", self.checkpoint_var, self._browse_checkpoint)
        
        ctk.CTkLabel(model, text="Backend").grid(row=1, column=0, sticky="w", pady=5)
        ctk.CTkOptionMenu(model, variable=self.backend_var, values=tuple(BACKEND_LABELS)).grid(row=1, column=1, sticky="w", pady=5, padx=5)
        
        ctk.CTkLabel(model, text="OpenCode API Key").grid(row=2, column=0, sticky="w", pady=5)
        ctk.CTkEntry(model, textvariable=self.api_key_var, show="*", width=250).grid(row=2, column=1, sticky="w", pady=5, padx=5)
        
        ctk.CTkLabel(model, text="OpenCode model").grid(row=3, column=0, sticky="w", pady=5)
        self.model_combo = ctk.CTkComboBox(model, variable=self.api_model_var, values=self.api_models_list, width=200, state="readonly")
        self.model_combo.grid(row=3, column=1, sticky="w", pady=5, padx=5)
        ctk.CTkButton(model, text="Fetch", command=self._fetch_models, width=60).grid(row=3, column=2, padx=5)
        
        ctk.CTkLabel(model, text="Voice").grid(row=4, column=0, sticky="w", pady=5)
        ctk.CTkOptionMenu(model, variable=self.voice_selection_var, values=tuple(selection.label for selection in VOICE_SELECTIONS)).grid(row=4, column=1, sticky="w", pady=5, padx=5)

        controls = create_section(content, "Generation Controls")
        self._labeled_entry(controls, 0, "Max tokens", self.max_tokens_var, 100)
        self._labeled_entry(controls, 2, "Temperature", self.temperature_var, 100)
        self._labeled_entry(controls, 4, "Top-k", self.top_k_var, 100)

        voice_output = create_section(content, "Voice Output")
        ctk.CTkLabel(voice_output, text="Base speed").grid(row=0, column=0, sticky="w", pady=5)
        ctk.CTkSlider(voice_output, from_=0.75, to=1.20, variable=self.voice_speed_var).grid(row=0, column=1, sticky="w", padx=10, pady=5)
        ctk.CTkLabel(voice_output, textvariable=self.voice_speed_var).grid(row=0, column=2, pady=5)
        ctk.CTkCheckBox(voice_output, text="Let Ru adjust pace around this speed", variable=self.auto_voice_pace_var).grid(row=1, column=0, columnspan=3, sticky="w", pady=5)

        speech = create_section(content, "Speech Input")
        ctk.CTkLabel(speech, text="Recognizer").grid(row=0, column=0, sticky="w", pady=5)
        ctk.CTkOptionMenu(speech, variable=self.speech_engine_var, values=tuple(SPEECH_ENGINE_LABELS)).grid(row=0, column=1, sticky="w", padx=5, pady=5)

        privacy = create_section(content, "Privacy and Memory")
        ctk.CTkCheckBox(privacy, text="Remember conversations", variable=self.remember_conversations_var).grid(row=0, column=0, sticky="w", pady=5)
        ctk.CTkCheckBox(privacy, text="Allow online search", variable=self.allow_web_search_var).grid(row=1, column=0, sticky="w", pady=5)
        ctk.CTkButton(privacy, text="Clear saved memory", command=self._clear_saved_memory).grid(row=2, column=0, sticky="w", pady=(5,10))

        ctk.CTkButton(dialog, text="Close", command=dialog.destroy).pack(pady=(0, 20))

    def _fetch_models(self) -> None:
        api_key = self.api_key_var.get().strip()
        if not api_key:
            self.status_var.set("Please enter OpenCode API Key first")
            return
            
        self.status_var.set("Fetching models...")
        self.root.update()
        
        def fetch_task():
            try:
                models = OpenAIRuntime.fetch_models(api_key, OPENCODE_BASE_URL)
                self.root.after(0, lambda: self._on_fetch_models_success(models))
            except Exception as e:
                self.root.after(0, lambda e=e: self.status_var.set(f"Fetch models error: {e}"))
                
        threading.Thread(target=fetch_task, daemon=True).start()
        
    def _on_fetch_models_success(self, models: list[str]) -> None:
        self.api_models_list = models
        try:
            if self.model_combo.winfo_exists():
                self.model_combo.configure(values=self.api_models_list)
                if models and self.api_model_var.get() not in models:
                    self.api_model_var.set(models[0])
        except (AttributeError, tk.TclError):
            pass  # model_combo chưa được tạo hoặc đã bị destroy
        self.status_var.set(f"Fetched {len(models)} models successfully")
    @staticmethod
    def _labeled_entry(parent: ctk.CTkFrame, column: int, label: str, variable: tk.StringVar, width: int) -> None:
        ctk.CTkLabel(parent, text=label).grid(row=0, column=column, padx=(0 if column == 0 else 12, 4))
        ctk.CTkEntry(parent, textvariable=variable, width=140).grid(row=0, column=column + 1)

    def _path_row(
        self,
        parent: ctk.CTkFrame,
        row: int,
        label: str,
        variable: tk.StringVar,
        browse_command: Any,
    ) -> None:
        ctk.CTkLabel(parent, text=label).grid(row=row, column=0, sticky="w", padx=6, pady=4)
        ctk.CTkEntry(parent, textvariable=variable, width=250).grid(row=row, column=1, sticky="ew", padx=4, pady=4)
        ctk.CTkButton(parent, text="Browse", command=browse_command).grid(row=row, column=2, padx=6, pady=4)

    def _browse_checkpoint(self) -> None:
        selected = filedialog.askdirectory(title="Select Llama Adapter Directory")
        if selected:
            self.checkpoint_var.set(selected)


    def _append_turn(self, turn: ChatTurn) -> None:
        self.transcript_text.configure(state="normal")
        if self._has_empty_state:
            self.transcript_text.delete("1.0", tk.END)
            self._has_empty_state = False
        for text, tag in conversation_display_segments(turn.prompt, turn.response):
            self.transcript_text.insert(tk.END, text, tag)
        self.transcript_text.see(tk.END)
        self.transcript_text.configure(state="disabled")

    def _begin_streaming_turn(self, prompt: str) -> None:
        self.transcript_text.configure(state="normal")
        if self._has_empty_state:
            self.transcript_text.delete("1.0", tk.END)
            self._has_empty_state = False
        self.transcript_text.insert(tk.END, "You\n", "user_label")
        self.transcript_text.insert(tk.END, f"{prompt}\n", "user_bubble")
        self.transcript_text.insert(tk.END, "Ru\n", "ru_label")
        self.transcript_text.tag_config("stream_anchor", elide=True)
        self.transcript_text.insert(tk.END, " ", "stream_anchor")
        self._stream_response_anchor = self.transcript_text.index("end-2c")
        self.transcript_text.insert(tk.END, "\n", "ru_bubble")
        self.transcript_text.see(tk.END)
        self.transcript_text.configure(state="disabled")

    def _replace_streaming_response(self, response: str) -> None:
        anchor = getattr(self, "_stream_response_anchor", None)
        if anchor is None:
            return
        response_start = f"{anchor} + 1c"
        self.transcript_text.configure(state="normal")
        self.transcript_text.delete(response_start, tk.END)
        self.transcript_text.insert(response_start, f"{response}\n\n", "ru_bubble")
        self.transcript_text.see(tk.END)
        self.transcript_text.configure(state="disabled")

    def _append_stream_delta(self, delta: str) -> None:
        self._stream_raw_response = append_stream_text(
            getattr(self, "_stream_raw_response", ""), delta
        )
        self._replace_streaming_response(safe_streamed_response(self._stream_raw_response))

    def _reset_streaming_turn(self) -> None:
        self._stream_raw_response = ""
        self._replace_streaming_response("")

    def _finish_streaming_turn(self, response: str) -> None:
        self._replace_streaming_response(response)
        self._clear_streaming_anchor()
        self._stream_raw_response = ""

    def _discard_streaming_turn(self) -> None:
        anchor = getattr(self, "_stream_response_anchor", None)
        if anchor is None:
            return
        self.transcript_text.configure(state="normal")
        self.transcript_text.delete(f"{anchor} - 1 lines", tk.END)
        self.transcript_text.configure(state="disabled")
        self._stream_response_anchor = None
        self._stream_raw_response = ""

    def _clear_streaming_anchor(self) -> None:
        anchor = getattr(self, "_stream_response_anchor", None)
        if anchor is not None:
            self.transcript_text.configure(state="normal")
            self.transcript_text.delete(anchor, f"{anchor} + 1c")
            self.transcript_text.configure(state="disabled")
        self._stream_response_anchor = None

    def _new_chat(self) -> None:
        if self.worker is not None and self.worker.is_alive():
            return
        self.session_turns.clear()
        self.last_turn = None
        self.last_turn_saved = False
        self.transcript_text.configure(state="normal")
        self.transcript_text.delete("1.0", tk.END)
        self.transcript_text.configure(state="disabled")
        self._has_empty_state = True
        self._show_empty_state()
        self._update_mark_button()
        self.status_var.set("New chat started")

    def _update_mark_button(self) -> None:
        state = "normal" if self.last_turn is not None and not self.last_turn_saved else "disabled"
        self.mark_button.configure(state=state)

    def _memory_store_for_session(self) -> StructuredMemoryStore:
        if self.memory_store is None:
            self.memory_store = StructuredMemoryStore(self.memory_db_path, index_path=self.memory_index_path)
            self.memory_store.migrate_legacy_transcripts(self.memory_texts_path)
        return self.memory_store

    def _clear_saved_memory(self) -> None:
        if not messagebox.askyesno(
            title="Clear saved memory",
            message="Delete locally saved conversation memory? This cannot be undone.",
            parent=self.root,
        ):
            return
        try:
            if self.memory_store is not None:
                self.memory_store.clear()
            MemoryStore.clear_artifacts(self.memory_index_path, self.memory_texts_path)
            if self.memory_db_path.is_file():
                self.memory_db_path.unlink()
        except OSError as error:
            self.status_var.set(f"Could not clear saved memory: {error}")
            return
        self.memory_store = None
        self.status_var.set("Saved conversation memory cleared")

    def _mark_last_response(self) -> None:
        if self.last_turn is None or self.last_turn_saved:
            self.status_var.set("There is no new response to save")
            return
        checkpoint = self.runner.checkpoint_path or Path(self.checkpoint_var.get())
        try:
            self.conversation_store.save(
                self.last_turn.prompt,
                self.last_turn.response,
                checkpoint,
            )
        except (OSError, ValueError) as error:
            self.status_var.set(f"Error saving example: {error}")
            return
        self.last_turn_saved = True
        self._update_mark_button()
        self.status_var.set(f"Saved useful response to {self.conversation_store.path}")

    def _toggle_voice_mode(self) -> None:
        self.voice_mode_active = not getattr(self, "voice_mode_active", False)
        if self.voice_mode_active:
            self.call_ru_button.configure(text="🛑 End Call")
            self.tts_enabled_var.set(True)
            self._toggle_recording()
        else:
            self.call_ru_button.configure(text="📞 Call Ru")
            self.status_var.set("Voice mode ended.")

    def _toggle_recording(self) -> None:
        if self.is_recording:
            return
        self.is_recording = True
        self.speak_button.configure(state="disabled", text="Listening...")
        self.status_var.set("Listening to your voice...")
        threading.Thread(target=self._record_worker, daemon=True).start()


    def _record_worker(self) -> None:
        try:
            with sr.Microphone() as source:
                self.microphone_recognizer.adjust_for_ambient_noise(source, duration=0.5)
                audio = self.microphone_recognizer.listen(source, timeout=5, phrase_time_limit=15)
            self.root.after(0, lambda: self.status_var.set("Recognizing..."))
            raw_audio = audio.get_raw_data(convert_rate=16_000, convert_width=2)
            text = self._selected_speech_recognizer().recognize(CapturedAudio(raw_audio))
            self.root.after(0, lambda t=text: self._on_record_success(t))
        except sr.WaitTimeoutError:
            self.root.after(0, lambda: self._on_record_error("Listening timed out."))
        except SpeechRecognitionError as error:
            self.root.after(0, lambda: self._on_record_error(str(error)))
        except Exception:
            self.root.after(0, lambda: self._on_record_error("Voice input failed."))

    def _speech_config(self) -> SpeechEngineConfig:
        engine_id = SPEECH_ENGINE_LABELS.get(self.speech_engine_var.get(), SPEECH_ENGINE_GOOGLE)
        return SpeechEngineConfig(engine_id=engine_id, model_dir=None)

    def _on_speech_settings_changed(self, *_args: object) -> None:
        self._speech_runtime = None
        self._speech_runtime_config = None
        self._save_gui_settings()

    def _selected_speech_recognizer(self) -> SpeechRecognizer:
        config = self._speech_config()
        if self._speech_runtime is None or self._speech_runtime_config != config:
            self._speech_runtime = build_speech_recognizer(
                config,
                google_recognizer=self.microphone_recognizer,
            )
            self._speech_runtime_config = config
        return self._speech_runtime
            
    def _on_record_success(self, text: str) -> None:
        self.is_recording = False
        self.speak_button.configure(state="normal", text="Voice input")
        self.status_var.set(f"Heard: {text}")
        current_text = self.prompt_text.get("1.0", tk.END).strip()
        new_text = f"{current_text} {text}".strip() if current_text else text
        self.prompt_text.delete("1.0", tk.END)
        self.prompt_text.insert(tk.END, new_text)
        if getattr(self, "voice_mode_active", False):
            self._start_generation()

    def _on_record_error(self, msg: str) -> None:
        self.is_recording = False
        self.speak_button.configure(state="normal", text="Voice input")
        self.status_var.set(msg)
        if getattr(self, "voice_mode_active", False):
            self.root.after(1000, self._toggle_recording)

    def _submit_from_keyboard(self, _event: object) -> str:
        self._start_generation()
        return "break"


    def _selected_voice_selection(self) -> VoiceSelection:
        return voice_selection_from_saved(self.voice_selection_var.get())


    def _start_generation(self) -> None:
        if self.worker is not None and self.worker.is_alive():
            return
        prompt = self.prompt_text.get("1.0", tk.END).strip()
        if not prompt:
            self.status_var.set("Write a message first")
            return
        try:
            settings = parse_generation_settings(
                self.max_tokens_var.get(), self.temperature_var.get(), self.top_k_var.get()
            )
            backend = BACKEND_LABELS[self.backend_var.get()]
        except ValueError as error:
            self.status_var.set(str(error))
            return
        except KeyError:
            self.status_var.set("Select a valid backend")
            return

        self._speech_generation += 1
        self._set_controls_enabled(False)
        self.status_var.set("Waiting for Ru...")
        checkpoint = self.checkpoint_var.get()
        remember_conversations = self.remember_conversations_var.get()
        allow_web_search = self.allow_web_search_var.get()
        # Capture Tk variables on main thread — Tk vars are not thread-safe to read from worker threads.
        tts_enabled = self.tts_enabled_var.get()
        api_key = self.api_key_var.get()
        api_model = self.api_model_var.get()
        voice_selection = self._selected_voice_selection()
        turns_snapshot = list(self.session_turns)
        self.worker = threading.Thread(
            target=self._generate_worker,
            args=(prompt, settings, checkpoint, backend, remember_conversations, allow_web_search, tts_enabled, api_key, api_model, voice_selection, turns_snapshot),
            daemon=True,
        )
        self.worker.start()

    def _set_controls_enabled(self, enabled: bool) -> None:
        state = "normal" if enabled else "disabled"
        self.generate_button.configure(state=state)
        self.speak_button.configure(state=state)
        self.new_chat_button.configure(state=state)
        self.settings_button.configure(state=state)
        self.tts_checkbox.configure(state=state)
        if not enabled:
            self.mark_button.configure(state="disabled")

    def _generate_worker(
        self,
        prompt: str,
        settings: tuple[int, float, int],
        checkpoint: str,
        backend: str,
        remember_conversations: bool,
        allow_web_search: bool,
        tts_enabled: bool,
        api_key: str,
        api_model: str,
        voice_selection: object,
        turns_snapshot: list,
    ) -> None:
        try:
            memory_context = None
            memory_store = self._memory_store_for_session() if remember_conversations else None
            if memory_store:
                memory_context = memory_store.render_context(
                    memory_store.retrieve(prompt, limit=5, max_chars=1200), max_chars=1200
                ) or None
            vts_catalog = self.vts_manager.current_catalog_sync() if hasattr(self, "vts_manager") else None

            api_seconds = 0.0
            voice_warm_seconds: float | None = None

            def record_api_timing(seconds: float) -> None:
                nonlocal api_seconds
                api_seconds += seconds

            is_cloud = backend == BACKEND_OPENCODE

            def stream_delta(delta: str) -> None:
                self.root.after(0, lambda delta=delta: self._append_stream_delta(delta))

            def reset_stream() -> None:
                self.root.after(0, self._reset_streaming_turn)

            with self.gpu_transition_lock:
                uses_voice = is_cloud and tts_enabled
                if uses_voice:
                    voice_warm_seconds = self.gpu_lifecycle.warm_voice_for_cloud(
                        voice_selection,
                        lambda status: self.root.after(
                            0, lambda status=status: self.status_var.set(status)
                        ),
                    )
                elif not is_cloud:
                    self.gpu_lifecycle.prepare_local_generation()
                self.runner.select_backend(backend)
                cache_hit = self.runner.is_cached(checkpoint)
                self.root.after(
                    0,
                    lambda cache_hit=cache_hit: self.status_var.set(
                        "Ru is replying..." if cache_hit else "Loading Ru..."
                    ),
                )
                started = perf_counter()
                self.runner.load(
                    checkpoint,
                    api_key,
                    model_name=api_model,
                )
                if is_cloud:
                    self.root.after(0, lambda prompt=prompt: self._begin_streaming_turn(prompt))
                result = self.runner.generate_reply(
                    prompt,
                    *settings,
                    turns_history=turns_snapshot,
                    memory_context=memory_context,
                    allow_web_search=allow_web_search,
                    vts_instruction=catalog_instruction(vts_catalog),
                    on_stream_delta=stream_delta if is_cloud else None,
                    on_stream_reset=reset_stream if is_cloud else None,
                    on_complete_timing=record_api_timing if is_cloud else None,
                )
                elapsed = perf_counter() - started
            self.root.after(
                0,
                lambda result=result, cache_hit=cache_hit, elapsed=elapsed, prompt=prompt, remember_conversations=remember_conversations, vts_catalog=vts_catalog, api_seconds=api_seconds, voice_warm_seconds=voice_warm_seconds, is_cloud=is_cloud: self._generation_finished(
                    result,
                    cache_hit,
                    elapsed,
                    prompt,
                    remember_conversations,
                    vts_catalog,
                    api_seconds=api_seconds if is_cloud else None,
                    voice_warm_seconds=voice_warm_seconds,
                    streamed=is_cloud,
                ),
            )
        except Exception as error:
            traceback.print_exc()
            message = str(error) if str(error) else repr(error)
            self.root.after(0, lambda message=message: self._generation_failed(message))

    def _generation_finished(
        self,
        result: str,
        cache_hit: bool,
        elapsed: float,
        original_prompt: str,
        remember_conversations: bool,
        vts_catalog: VtsCatalog | None = None,
        *,
        api_seconds: float | None = None,
        voice_warm_seconds: float | None = None,
        streamed: bool = False,
    ) -> None:
        try:
            if not result.strip():
                self._generation_failed("model returned an empty response")
                return
            pace_result, pace = parse_voice_pace(result)
            clean_result, catalog_codes = parse_catalog_reply(pace_result, vts_catalog)
            vts_actions = requested_vts_actions(original_prompt)
            if not clean_result.strip():
                self._generation_failed("model returned no visible response")
                return
            turn = ChatTurn(original_prompt, clean_result)
            self.session_turns.append(turn)
            self.last_turn = turn
            self.last_turn_saved = False
            if streamed:
                self._finish_streaming_turn(clean_result)
            else:
                self._append_turn(turn)

            if (
                remember_conversations
                and self.remember_conversations_var.get()
                and self.memory_store
            ):
                self.memory_store.remember_turn(original_prompt, clean_result)

            status_kwargs = {
                "cache_hit": cache_hit,
                "seconds": elapsed,
                "api_seconds": api_seconds,
                "voice_warm_seconds": voice_warm_seconds,
            }
            self.status_var.set(format_generation_status(**status_kwargs))
            self._set_controls_enabled(True)
            self._update_mark_button()
            self.prompt_text.delete("1.0", tk.END)
            self.prompt_text.focus_set()

            if hasattr(self, "vts_manager"):
                if catalog_codes and hasattr(self.vts_manager, "trigger_catalog_actions_sync"):
                    self.vts_manager.trigger_catalog_actions_sync(vts_catalog, catalog_codes)
                self.vts_manager.trigger_actions_sync(vts_actions)

            if self.tts_enabled_var.get():
                def update_first_voice(seconds: float) -> None:
                    self.root.after(
                        0,
                        lambda: self.status_var.set(
                            format_generation_status(
                                **status_kwargs,
                                first_voice_seconds=seconds,
                            )
                        ),
                    )

                threading.Thread(
                    target=self._play_tts,
                    args=(clean_result, update_first_voice, effective_voice_speed(
                        self.voice_speed_var.get(),
                        auto_pace_enabled=self.auto_voice_pace_var.get(),
                        pace=pace,
                    )),
                    daemon=True,
                ).start()
        except Exception as error:
            logger.warning("Error in _generation_finished: %s", error, exc_info=True)
            self._generation_failed(str(error) or repr(error))

    def _play_tts(self, text: str, on_first_audio_timing=None, speed: float = DEFAULT_VOICE_SPEED) -> None:
        try:
            clean_text = clean_tts_text(text)
            if not clean_text:
                return

            self.root.after(0, lambda: self.status_var.set("Preparing voice..."))
            
            selection = self._selected_voice_selection()
            self.gpu_lifecycle.prepare_speech(selection)
            self._play_voice_chunks(
                split_tts_chunks(clean_text),
                selection=selection,
                speed=speed,
                on_first_audio_timing=on_first_audio_timing,
            )

        except Exception as error:
            self.root.after(0, lambda e=str(error): self.status_var.set(f"Voice unavailable: {e}"))
            if getattr(self, "voice_mode_active", False):
                self.root.after(1000, self._toggle_recording)

    def _play_voice_chunks(
        self,
        chunks: list[str],
        *,
        selection: VoiceSelection = DEFAULT_VOICE_SELECTION,
        speed: float = DEFAULT_VOICE_SPEED,
        on_first_audio_timing=None,
    ) -> None:
        if not chunks:
            return

        generation = self._speech_generation
        files: queue.Queue[object] = queue.Queue(maxsize=TTS_PREFETCH_WAVS)
        stop_requested = threading.Event()
        end_of_queue = object()
        first_audio_reported = False

        def remove_audio_file(audio_file: object) -> None:
            if isinstance(audio_file, str):
                try:
                    os.remove(audio_file)
                except OSError:
                    pass

        def publish(item: object) -> bool:
            while not stop_requested.is_set() and generation == self._speech_generation:
                try:
                    files.put(item, timeout=0.1)
                    return True
                except queue.Full:
                    continue
            return False

        def synthesize_chunks() -> None:
            nonlocal first_audio_reported
            try:
                for chunk in chunks:
                    if stop_requested.is_set() or generation != self._speech_generation:
                        return
                    started = perf_counter()
                    audio_file = request_tts_audio(chunk, selection, speed=speed)
                    if not first_audio_reported:
                        first_audio_reported = True
                        if on_first_audio_timing is not None:
                            on_first_audio_timing(max(0.0, perf_counter() - started))
                    if not publish(audio_file):
                        remove_audio_file(audio_file)
                        return
            except Exception as error:
                publish(("error", str(error)))
            finally:
                publish(end_of_queue)

        if hasattr(self, "vts_manager"):
            self.vts_manager.set_speaking(True)
        producer = threading.Thread(target=synthesize_chunks, daemon=True)
        producer.start()
        try:
            while generation == self._speech_generation:
                item = files.get()
                if item is end_of_queue:
                    break
                if isinstance(item, tuple) and item[:1] == ("error",):
                    self.root.after(
                        0,
                        lambda detail=str(item[1]): self.status_var.set(f"Voice unavailable: {detail}"),
                    )
                    break
                if not isinstance(item, str):
                    continue
                try:
                    play_wav_blocking(item)
                finally:
                    remove_audio_file(item)
        finally:
            stop_requested.set()
            while True:
                try:
                    remove_audio_file(files.get_nowait())
                except queue.Empty:
                    break
            if hasattr(self, "vts_manager"):
                self.vts_manager.set_speaking(False)
            if generation == self._speech_generation and getattr(self, "voice_mode_active", False):
                self.root.after(500, self._toggle_recording)

    def _generation_failed(self, message: str) -> None:
        self._discard_streaming_turn()
        self.status_var.set(f"Error: {message}")
        self._set_controls_enabled(True)
        self._update_mark_button()
        if getattr(self, "voice_mode_active", False):
            self.root.after(1000, self._toggle_recording)


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    root = ctk.CTk()
    app = RuApp(root)
    try:
        root.mainloop()
    finally:
        app.vts_manager.shutdown()
        app.runner.release_gpu()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
