from __future__ import annotations

import re
import queue
import socket
import subprocess
import sys
import threading
import tkinter as tk
from time import monotonic, perf_counter, sleep
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

import os
import json
import speech_recognition as sr
from .vts_manager import VTSManager
from .vts_actions import VTS_ACTION_INSTRUCTION, requested_vts_actions
from .vts_catalog import VtsCatalog, catalog_instruction, parse_catalog_reply
import pygame

import torch

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
    PALETTE,
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

PROJECT_ROOT = Path(__file__).resolve().parents[2]
MODEL_NAME = "Ru"
GUI_SETTINGS_PATH = PROJECT_ROOT / "data" / "gui_settings.json"
DEFAULT_MAX_NEW_TOKENS = "512"
DEFAULT_ALLOW_WEB_SEARCH = True
TTS_PREFETCH_WAVS = 2
BACKEND_TRANSFORMERS = "transformers"
BACKEND_GGUF = "gguf"
BACKEND_OPENCODE = "opencode"
BACKEND_LABELS = {
    "GGUF (default)": BACKEND_GGUF,
    "Transformers (fallback)": BACKEND_TRANSFORMERS,
    "OpenCode Zen API": BACKEND_OPENCODE,
}

from .openai_runtime import OPENCODE_BASE_URL, OpenAIRuntime


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
            detail = response.json().get("error")
        except Exception:
            detail = None
        suffix = f": {detail}" if isinstance(detail, str) and detail else ""
        raise RuntimeError(f"Voice server failed (HTTP {response.status_code}){suffix}")
    try:
        audio_file = response.json().get("audio_path")
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
        return execute_reply(
        system_prompt = RU_CLOUD_SYSTEM_PROMPT if self.backend == BACKEND_OPENCODE else RU_SYSTEM_PROMPT
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
    def __init__(self, root: tk.Tk) -> None:
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
        self.checkpoint_var = tk.StringVar(value=str(checkpoint))
        
        saved_settings = {}
        if GUI_SETTINGS_PATH.is_file():
            try:
                with open(GUI_SETTINGS_PATH, "r", encoding="utf-8") as f:
                    saved_settings = json.load(f)
            except Exception:
                pass
                
        saved_backend = saved_settings.get("backend", "GGUF (default)")
        if saved_backend == "OpenAI API":
            saved_backend = "OpenCode Zen API"
        if saved_backend not in BACKEND_LABELS:
            saved_backend = "GGUF (default)"
        self.api_key_var = tk.StringVar(value=saved_settings.get("api_key", ""))
        self.api_model_var = tk.StringVar(
            value=saved_settings.get("api_model", "deepseek-v4-flash-free")
        )
        self.backend_var = tk.StringVar(value=saved_backend)
        saved_selection = voice_selection_from_saved(saved_settings.get("voice_profile"))
        self.voice_selection_var = tk.StringVar(value=saved_selection.label)
        self.voice_speed_var = tk.DoubleVar(value=voice_speed_from_saved(saved_settings.get("voice_speed")))
        self.auto_voice_pace_var = tk.BooleanVar(value=bool(saved_settings.get("auto_voice_pace", True)))
        saved_speech_config = speech_settings_from_mapping(saved_settings)
        saved_speech_label = next(
            label
            for label, engine_id in SPEECH_ENGINE_LABELS.items()
            if engine_id == saved_speech_config.engine_id
        )
        self.speech_engine_var = tk.StringVar(value=saved_speech_label)
        self.speech_model_dir_var = tk.StringVar(
            value=str(saved_speech_config.model_dir) if saved_speech_config.model_dir else ""
        )

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
                GUI_SETTINGS_PATH.parent.mkdir(parents=True, exist_ok=True)
                with open(GUI_SETTINGS_PATH, "w", encoding="utf-8") as f:
                    json.dump(settings, f)
            except Exception:
                pass

        self._save_gui_settings = _save_settings
        self.api_key_var.trace_add("write", _save_settings)
        self.api_model_var.trace_add("write", _save_settings)
        self.backend_var.trace_add("write", _save_settings)
        self.voice_selection_var.trace_add("write", _save_settings)
        self.voice_speed_var.trace_add("write", _save_settings)
        self.auto_voice_pace_var.trace_add("write", _save_settings)
        self.speech_engine_var.trace_add("write", self._on_speech_settings_changed)
        self.speech_model_dir_var.trace_add("write", self._on_speech_settings_changed)
        _save_settings()
        
        self.max_tokens_var = tk.StringVar(value=DEFAULT_MAX_NEW_TOKENS)
        self.temperature_var = tk.StringVar(value="0.7")
        self.top_k_var = tk.StringVar(value="20")
        self.status_var = tk.StringVar(
            value=f"CUDA available: {'yes' if torch.cuda.is_available() else 'no'} | Ready"
        )
        self.api_models_list: list[str] = [self.api_model_var.get()]
        self.tts_enabled_var = tk.BooleanVar(value=True)
        self.remember_conversations_var = tk.BooleanVar(value=True)
        self.allow_web_search_var = tk.BooleanVar(value=DEFAULT_ALLOW_WEB_SEARCH)
        pygame.mixer.init()
        self.vts_manager = VTSManager()
        threading.Thread(target=self.vts_manager.run, daemon=True).start()
        self.is_recording = False
        self.microphone_recognizer = sr.Recognizer()
        self._speech_runtime: SpeechRecognizer | None = None
        self._speech_runtime_config: SpeechEngineConfig | None = None
        self.generate_button: ttk.Button
        self.new_chat_button: ttk.Button
        self.mark_button: ttk.Button
        self.settings_button: ttk.Button
        self.prompt_text: tk.Text
        self.transcript_text: tk.Text
        self._has_empty_state = True
        self._build()

    def _build(self) -> None:
        self.root.title(MODEL_NAME)
        self.root.geometry("900x720")
        self.root.minsize(680, 540)
        self.root.configure(background=PALETTE["background"])
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(0, weight=1)
        self._configure_styles()

        shell = tk.Frame(self.root, background=PALETTE["background"])
        shell.grid(row=0, column=0, sticky="nsew", padx=20, pady=18)
        shell.columnconfigure(0, weight=1)
        shell.rowconfigure(1, weight=1)

        header = tk.Frame(shell, background=PALETTE["background"])
        header.grid(row=0, column=0, sticky="ew", pady=(0, 12))
        header.columnconfigure(1, weight=1)
        tk.Label(
            header,
            text=MODEL_NAME,
            background=PALETTE["background"],
            foreground=PALETTE["ink"],
            font=("Segoe UI Semibold", 20),
        ).grid(row=0, column=0, sticky="w")
        tk.Label(
            header,
            text="Local Llama",
            background=PALETTE["background"],
            foreground=PALETTE["muted"],
            font=("Segoe UI", 10),
        ).grid(row=1, column=0, sticky="w")
        self.settings_button = ttk.Button(
            header,
            text="Settings",
            command=self._open_settings,
            style="Ru.Quiet.TButton",
        )
        self.settings_button.grid(row=0, column=2, rowspan=2, sticky="e")

        content = tk.Frame(shell, background=PALETTE["background"])
        content.grid(row=1, column=0, sticky="nsew")
        content.columnconfigure(0, weight=1)
        content.rowconfigure(0, weight=1)

        transcript_frame = tk.Frame(content, background=PALETTE["surface"], highlightthickness=0)
        transcript_frame.grid(row=0, column=0, sticky="nsew")
        transcript_frame.columnconfigure(0, weight=1)
        transcript_frame.rowconfigure(0, weight=1)
        self.transcript_text = tk.Text(
            transcript_frame,
            wrap="word",
            state="disabled",
            background=PALETTE["surface"],
            foreground=PALETTE["ink"],
            borderwidth=0,
            highlightthickness=0,
            font=("Segoe UI", 11),
            padx=20,
            pady=18,
            spacing1=2,
            spacing3=10,
        )
        self.transcript_text.grid(row=0, column=0, sticky="nsew")
        self.transcript_text.tag_configure(
            "ru_label", foreground=PALETTE["accent"], font=("Segoe UI Semibold", 10)
        )
        self.transcript_text.tag_configure(
            "ru_bubble", background=PALETTE["ru_bubble"], lmargin1=12, lmargin2=12, rmargin=72
        )
        self.transcript_text.tag_configure(
            "user_label", foreground=PALETTE["muted"], font=("Segoe UI Semibold", 10), justify="right"
        )
        self.transcript_text.tag_configure(
            "user_bubble", background=PALETTE["user_bubble"], lmargin1=72, lmargin2=72, rmargin=12, justify="right"
        )
        self.transcript_text.tag_configure(
            "empty", foreground=PALETTE["muted"], justify="center", spacing1=36
        )
        transcript_scrollbar = ttk.Scrollbar(transcript_frame, orient="vertical", command=self.transcript_text.yview)
        transcript_scrollbar.grid(row=0, column=1, sticky="ns")
        self.transcript_text.configure(yscrollcommand=transcript_scrollbar.set)
        self._show_empty_state()

        composer = tk.Frame(content, background=PALETTE["background"])
        composer.grid(row=1, column=0, sticky="ew", pady=(12, 0))
        composer.columnconfigure(0, weight=1)
        tk.Label(
            composer,
            text="Write to Ru",
            background=PALETTE["background"],
            foreground=PALETTE["muted"],
            font=("Segoe UI", 10),
        ).grid(row=0, column=0, sticky="w", pady=(0, 5))
        self.prompt_text = tk.Text(
            composer,
            height=4,
            wrap="word",
            background=PALETTE["surface"],
            foreground=PALETTE["ink"],
            borderwidth=0,
            highlightthickness=1,
            highlightbackground="#E6DFEA",
            highlightcolor=PALETTE["accent"],
            font=("Segoe UI", 11),
            padx=12,
            pady=10,
        )
        self.prompt_text.grid(row=1, column=0, sticky="ew")
        self.prompt_text.bind("<Control-Return>", self._submit_from_keyboard)

        actions = tk.Frame(composer, background=PALETTE["background"])
        actions.grid(row=2, column=0, sticky="ew", pady=(8, 0))
        self.generate_button = ttk.Button(actions, text="Send", command=self._start_generation, style="Ru.Primary.TButton")
        self.generate_button.grid(row=0, column=0, padx=(0, 6))
        self.speak_button = ttk.Button(actions, text="Voice input", command=self._toggle_recording, style="Ru.Quiet.TButton")
        self.speak_button.grid(row=0, column=1, padx=(0, 6))
        self.call_ru_button = ttk.Button(actions, text="📞 Call Ru", command=self._toggle_voice_mode, style="Ru.Quiet.TButton")
        self.call_ru_button.grid(row=0, column=2, padx=(0, 6))
        self.new_chat_button = ttk.Button(actions, text="New chat", command=self._new_chat, style="Ru.Quiet.TButton")
        self.new_chat_button.grid(row=0, column=3, padx=(0, 6))
        self.mark_button = ttk.Button(
            actions, text="Save useful reply", command=self._mark_last_response, state="disabled", style="Ru.Quiet.TButton"
        )
        self.mark_button.grid(row=0, column=4, padx=(0, 6))
        self.tts_checkbox = ttk.Checkbutton(actions, text="Ru voice", variable=self.tts_enabled_var)
        self.tts_checkbox.grid(row=0, column=5, padx=(6, 0))
        ttk.Label(composer, textvariable=self.status_var, style="Ru.Status.TLabel").grid(
            row=3, column=0, sticky="w", pady=(8, 0)
        )

    def _configure_styles(self) -> None:
        style = ttk.Style(self.root)
        style.configure("Ru.Primary.TButton", font=("Segoe UI Semibold", 10), padding=(14, 7))
        style.configure("Ru.Quiet.TButton", font=("Segoe UI", 10), padding=(10, 7))
        style.configure(
            "Ru.Status.TLabel",
            background=PALETTE["background"],
            foreground=PALETTE["muted"],
            font=("Segoe UI", 9),
        )

    def _show_empty_state(self) -> None:
        self.transcript_text.configure(state="normal")
        self.transcript_text.insert(
            tk.END,
            "Chào anh, em ở đây. Hôm nay anh muốn kể em nghe chuyện gì?",
            "empty",
        )
        self.transcript_text.configure(state="disabled")

    def _open_settings(self) -> None:
        dialog = tk.Toplevel(self.root)
        dialog.title("Ru settings")
        dialog.configure(background=PALETTE["background"])
        dialog.transient(self.root)
        dialog.resizable(False, False)
        dialog.grab_set()

        content = ttk.Frame(dialog, padding=16)
        content.grid(row=0, column=0, sticky="nsew")
        model = ttk.LabelFrame(content, text="Model")
        model.grid(row=0, column=0, sticky="ew")
        model.columnconfigure(1, weight=1)
        self._path_row(model, 0, "Checkpoint / adapter", self.checkpoint_var, self._browse_checkpoint)
        backend = ttk.Frame(model)
        backend.grid(row=1, column=0, columnspan=3, sticky="w", padx=6, pady=(2, 4))
        ttk.Label(backend, text="Backend").grid(row=0, column=0, padx=(0, 6))
        ttk.Combobox(
            backend,
            textvariable=self.backend_var,
            values=tuple(BACKEND_LABELS),
            state="readonly",
            width=24,
        ).grid(row=0, column=1)
        ttk.Label(backend, text="OpenCode API Key").grid(row=0, column=2, padx=(12, 6))
        ttk.Entry(backend, textvariable=self.api_key_var, show="*", width=24).grid(row=0, column=3)

        ttk.Label(backend, text="OpenCode model").grid(row=1, column=2, padx=(12, 6), pady=4)
        self.model_combo = ttk.Combobox(backend, textvariable=self.api_model_var, values=self.api_models_list, width=22)
        self.model_combo.grid(row=1, column=3, pady=4)
        
        ttk.Label(backend, text="Voice").grid(row=1, column=0, padx=(0, 6))
        ttk.Combobox(
            backend,
            textvariable=self.voice_selection_var,
            values=tuple(selection.label for selection in VOICE_SELECTIONS),
            state="readonly",
            width=24,
        ).grid(row=1, column=1)

        voice_output = ttk.LabelFrame(content, text="Voice output")
        voice_output.grid(row=1, column=0, sticky="ew", pady=(12, 0))
        voice_output.columnconfigure(1, weight=1)
        ttk.Label(voice_output, text="Base speed").grid(row=0, column=0, sticky="w", padx=8, pady=(8, 4))
        ttk.Scale(
            voice_output,
            from_=0.75,
            to=1.20,
            variable=self.voice_speed_var,
            orient="horizontal",
        ).grid(row=0, column=1, sticky="ew", padx=6, pady=(8, 4))
        ttk.Label(voice_output, textvariable=self.voice_speed_var, width=5).grid(row=0, column=2, padx=(0, 8), pady=(8, 4))
        ttk.Checkbutton(
            voice_output,
            text="Let Ru adjust pace around this speed",
            variable=self.auto_voice_pace_var,
        ).grid(row=1, column=0, columnspan=3, sticky="w", padx=8, pady=(4, 8))
        
        ttk.Button(backend, text="Fetch", command=self._fetch_models, width=6).grid(row=1, column=4, padx=(4, 0))

        controls = ttk.Frame(model)
        controls.grid(row=3, column=0, columnspan=3, sticky="w", padx=6, pady=(2, 8))
        self._labeled_entry(controls, 0, "Max tokens", self.max_tokens_var, 8)
        self._labeled_entry(controls, 2, "Temperature", self.temperature_var, 8)
        self._labeled_entry(controls, 4, "Top-k", self.top_k_var, 8)

        speech = ttk.LabelFrame(content, text="Speech input")
        speech.grid(row=2, column=0, sticky="ew", pady=(12, 0))
        speech.columnconfigure(1, weight=1)
        ttk.Label(speech, text="Recognizer").grid(row=0, column=0, sticky="w", padx=6, pady=4)
        ttk.Combobox(
            speech,
            textvariable=self.speech_engine_var,
            values=tuple(SPEECH_ENGINE_LABELS),
            state="readonly",
            width=24,
        ).grid(row=0, column=1, sticky="w", padx=4, pady=4)
        self._path_row(speech, 1, "Offline model folder", self.speech_model_dir_var, self._browse_speech_model)

        privacy = ttk.LabelFrame(content, text="Privacy and memory")
        privacy.grid(row=3, column=0, sticky="ew", pady=(12, 0))
        ttk.Checkbutton(privacy, text="Remember conversations", variable=self.remember_conversations_var).grid(
            row=0, column=0, sticky="w", padx=8, pady=(8, 4)
        )
        ttk.Checkbutton(privacy, text="Allow online search", variable=self.allow_web_search_var).grid(
            row=1, column=0, sticky="w", padx=8, pady=4
        )
        ttk.Button(privacy, text="Clear saved memory", command=self._clear_saved_memory).grid(
            row=2, column=0, sticky="w", padx=8, pady=(4, 8)
        )
        ttk.Button(content, text="Close", command=dialog.destroy, style="Ru.Primary.TButton").grid(
            row=4, column=0, sticky="e", pady=(14, 0)
        )

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
        if hasattr(self, "model_combo"):
            self.model_combo['values'] = self.api_models_list
            if models and self.api_model_var.get() not in models:
                self.api_model_var.set(models[0])
        self.status_var.set(f"Fetched {len(models)} models successfully")
    @staticmethod
    def _labeled_entry(parent: ttk.Frame, column: int, label: str, variable: tk.StringVar, width: int) -> None:
        ttk.Label(parent, text=label).grid(row=0, column=column, padx=(0 if column == 0 else 12, 4))
        ttk.Entry(parent, textvariable=variable, width=width).grid(row=0, column=column + 1)

    def _path_row(
        self,
        parent: ttk.LabelFrame,
        row: int,
        label: str,
        variable: tk.StringVar,
        browse_command: Any,
    ) -> None:
        ttk.Label(parent, text=label).grid(row=row, column=0, sticky="w", padx=6, pady=4)
        ttk.Entry(parent, textvariable=variable).grid(row=row, column=1, sticky="ew", padx=4, pady=4)
        ttk.Button(parent, text="Browse", command=browse_command).grid(row=row, column=2, padx=6, pady=4)

    def _browse_checkpoint(self) -> None:
        selected = filedialog.askdirectory(title="Select Llama Adapter Directory")
        if selected:
            self.checkpoint_var.set(selected)

    def _browse_speech_model(self) -> None:
        selected = filedialog.askdirectory(title="Select Sherpa-ONNX Model Directory")
        if selected:
            self.speech_model_dir_var.set(selected)

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
        self.transcript_text.insert(tk.END, f"{prompt}\n\n", "user_bubble")
        self.transcript_text.insert(tk.END, "Ru\n", "ru_label")
        self.transcript_text.tag_configure("stream_anchor", elide=True)
        self.transcript_text.insert(tk.END, " ", "stream_anchor")
        self._stream_response_anchor = self.transcript_text.index("end-2c")
        self.transcript_text.insert(tk.END, "\n\n", "ru_bubble")
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
        model_dir = self.speech_model_dir_var.get().strip()
        return SpeechEngineConfig(engine_id=engine_id, model_dir=Path(model_dir) if model_dir else None)

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
        self.worker = threading.Thread(
            target=self._generate_worker,
            args=(prompt, settings, checkpoint, backend, remember_conversations, allow_web_search),
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
                uses_voice = is_cloud and self.tts_enabled_var.get()
                if uses_voice:
                    voice_warm_seconds = self.gpu_lifecycle.warm_voice_for_cloud(
                        self._selected_voice_selection(),
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
                    self.api_key_var.get(),
                    model_name=self.api_model_var.get(),
                )
                if is_cloud:
                    self.root.after(0, lambda prompt=prompt: self._begin_streaming_turn(prompt))
                result = self.runner.generate_reply(
                    prompt,
                    *settings,
                    turns_history=list(self.session_turns),
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
            import traceback

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
    root = tk.Tk()
    app = RuApp(root)
    try:
        root.mainloop()
    finally:
        app.vts_manager.shutdown()
        app.runner.release_gpu()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
