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
from .conversation_context import build_conversation_messages
from .conversation_store import ConversationStore
from .llama3_1_runtime import (
    clear_llama_generation_cache,
    dependency_status,
    generate_llama_response_hf,
    is_llama_generation_cached,
)
from .llama_cpp_runtime import LlamaCppRuntime
from .gui_presentation import PALETTE, conversation_display_segments, format_generation_status
from .ru_style import RU_SYSTEM_PROMPT
from .memory import MemoryStore
from .search import perform_web_search
from .tts_chunks import split_tts_chunks

PROJECT_ROOT = Path(__file__).resolve().parents[2]
MODEL_NAME = "Ru"
GUI_SETTINGS_PATH = PROJECT_ROOT / "data" / "gui_settings.json"
GWEN_TTS_BASE_URL = "http://127.0.0.1:5051"
GWEN_TTS_SERVER_URL = f"{GWEN_TTS_BASE_URL}/tts"
GWEN_TTS_HOST = "127.0.0.1"
GWEN_TTS_PORT = 5051
GWEN_TTS_PYTHON = PROJECT_ROOT / "gwen_env" / "Scripts" / "python.exe"
GWEN_TTS_SERVER_SCRIPT = PROJECT_ROOT / "gwen_tts_server.py"
VOICE_PROFILE_OPTIONS = {
    "Ru (Tsukuyomi)": "ru",
    "Yến Nhi (Gwen)": "yen_nhi",
}
DEFAULT_VOICE_PROFILE = "ru"
DEFAULT_MAX_NEW_TOKENS = "128"
BACKEND_TRANSFORMERS = "transformers"
BACKEND_GGUF = "gguf"
BACKEND_OPENAI = "openai"
BACKEND_OPENCODE = "opencode"
BACKEND_LABELS = {
    "GGUF (default)": BACKEND_GGUF,
    "Transformers (fallback)": BACKEND_TRANSFORMERS,
    "OpenAI API": BACKEND_OPENAI,
    "OpenCode Zen API": BACKEND_OPENCODE,
}

from .openai_runtime import OpenAIRuntime


def clean_tts_text(text: str) -> str:
    clean_text = re.sub(r"\[SEARCH:.*?\]", "", text).strip()
    clean_text = re.sub(r"<thought>.*?</thought>", "", clean_text, flags=re.DOTALL)
    clean_text = re.sub(r"<think>.*?</think>", "", clean_text, flags=re.DOTALL)
    return clean_text.replace("*", "").strip()


def gwen_tts_server_available() -> bool:
    try:
        with socket.create_connection((GWEN_TTS_HOST, GWEN_TTS_PORT), timeout=0.2):
            return True
    except OSError:
        return False


def ensure_gwen_tts_server(*, process_factory=None, sleep=sleep, monotonic=monotonic) -> None:
    if gwen_tts_server_available():
        return
    if not GWEN_TTS_PYTHON.is_file() or not GWEN_TTS_SERVER_SCRIPT.is_file():
        raise RuntimeError("Gwen runtime is missing. Reinstall the local voice environment.")

    process_factory = process_factory or subprocess.Popen
    creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) | getattr(subprocess, "DETACHED_PROCESS", 0)
    process_factory(
        [str(GWEN_TTS_PYTHON), str(GWEN_TTS_SERVER_SCRIPT)],
        cwd=str(PROJECT_ROOT),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=creation_flags,
    )

    deadline = monotonic() + 12
    while monotonic() < deadline:
        if gwen_tts_server_available():
            return
        sleep(0.2)
    raise RuntimeError("Gwen voice service did not start. Check the local voice environment.")


def request_tts_audio(
    text: str,
    fast_mode: bool = False,
    voice_profile: str = DEFAULT_VOICE_PROFILE,
    *,
    post=None,
) -> str:
    if post is None:
        import requests

        post = requests.post
    try:
        response = post(
            GWEN_TTS_SERVER_URL,
            json={"text": text, "fast_mode": fast_mode, "voice_profile": voice_profile},
            timeout=90,
        )
    except Exception as error:
        raise RuntimeError("Voice server is offline. Start Gwen TTS and try again.") from error
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


def request_gwen_control(path: str, payload: dict[str, str] | None = None, *, post=None) -> dict[str, bool]:
    if post is None:
        import requests

        post = requests.post
    try:
        response = post(f"{GWEN_TTS_BASE_URL}{path}", json=payload or {}, timeout=30)
    except Exception as error:
        raise RuntimeError("Voice server is offline. Start Gwen TTS and try again.") from error
    if response.status_code != 200:
        raise RuntimeError("Gwen voice service rejected lifecycle request.")
    try:
        payload = response.json()
    except Exception as error:
        raise RuntimeError("Gwen voice service returned an invalid lifecycle response.") from error
    if not isinstance(payload, dict):
        raise RuntimeError("Gwen voice service returned an invalid lifecycle response.")
    return payload


def play_wav_blocking(audio_file: str) -> None:
    sound = pygame.mixer.Sound(audio_file)
    sound.play()
    pygame.time.wait(int(sound.get_length() * 1000))


def default_paths() -> Path:
    candidates = (
        PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v6.1",
        PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v6",
        PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v5",
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
        if backend not in (BACKEND_TRANSFORMERS, BACKEND_GGUF, BACKEND_OPENAI, BACKEND_OPENCODE):
            raise ValueError(f"unsupported backend: {backend}")
        self.backend = backend

    def load(self, checkpoint_path: str | Path, api_key: str = "", api_url: str = "https://api.openai.com/v1", model_name: str = "gpt-4o-mini") -> str:
        if self.backend == BACKEND_OPENCODE:
            self.openai_runtime = OpenAIRuntime(api_key, base_url="https://opencode.ai/zen/v1", model_name=model_name)
            return "Ready (OpenCode Zen API)"
        if self.backend == BACKEND_OPENAI:
            self.openai_runtime = OpenAIRuntime(api_key, base_url=api_url, model_name=model_name)
            return "Ready (External API)"
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
        if self.backend in (BACKEND_OPENAI, BACKEND_OPENCODE):
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
    ) -> str:
        if self.backend in (BACKEND_OPENAI, BACKEND_OPENCODE) and self.openai_runtime:
            return self.openai_runtime.generate(messages, max_new_tokens, temperature, top_k)
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
    ) -> str:
        messages = build_conversation_messages(
            RU_SYSTEM_PROMPT + VTS_ACTION_INSTRUCTION + vts_instruction,
            prompt,
            turns_history or (),
            memory_context=memory_context,
        )

        response = self._generate(tuple(messages), max_new_tokens, temperature, top_k)

        search_match = re.search(r"\[SEARCH:\s*(.*?)\]", response)
        if allow_web_search and search_match:
            query = search_match.group(1).strip()
            search_results = perform_web_search(query)
            messages = messages[:-1] + (
                (
                    "user",
                    f"{prompt}\n\n[Untrusted web reference for '{query}':\n{search_results}]",
                ),
            )

            response = self._generate(tuple(messages), max_new_tokens, temperature, top_k)
        return response


class RuApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.runner = ModelRunner()
        self.worker: threading.Thread | None = None
        self.gpu_transition_lock = threading.Lock()
        self._speech_generation = 0
        self.session_turns: list[ChatTurn] = []
        self.last_turn: ChatTurn | None = None
        self.last_turn_saved = False
        self.voice_mode_active = False
        self.conversation_store = ConversationStore(
            PROJECT_ROOT / "data" / "conversations" / "approved.jsonl"
        )
        self.memory_index_path = PROJECT_ROOT / "data" / "conversations" / "memory_index.faiss"
        self.memory_texts_path = PROJECT_ROOT / "data" / "conversations" / "memory_texts.json"
        self.memory_store: MemoryStore | None = None
        checkpoint = default_paths()
        self.checkpoint_var = tk.StringVar(value=str(checkpoint))
        
        saved_settings = {}
        if GUI_SETTINGS_PATH.is_file():
            try:
                with open(GUI_SETTINGS_PATH, "r", encoding="utf-8") as f:
                    saved_settings = json.load(f)
            except Exception:
                pass
                
        self.api_key_var = tk.StringVar(value=saved_settings.get("api_key", ""))
        self.api_url_var = tk.StringVar(value=saved_settings.get("api_url", "https://api.openai.com/v1"))
        self.api_model_var = tk.StringVar(value=saved_settings.get("api_model", "gpt-4o-mini"))
        self.backend_var = tk.StringVar(value=saved_settings.get("backend", "GGUF (default)"))
        saved_profile = saved_settings.get("voice_profile", DEFAULT_VOICE_PROFILE)
        if saved_profile not in VOICE_PROFILE_OPTIONS.values():
            saved_profile = DEFAULT_VOICE_PROFILE
        saved_profile_label = next(
            label for label, profile in VOICE_PROFILE_OPTIONS.items() if profile == saved_profile
        )
        self.voice_profile_var = tk.StringVar(value=saved_profile_label)

        def _save_settings(*args) -> None:
            settings = {
                "api_key": self.api_key_var.get(),
                "api_url": self.api_url_var.get(),
                "api_model": self.api_model_var.get(),
                "backend": self.backend_var.get(),
                "voice_profile": self._selected_voice_profile(),
            }
            try:
                GUI_SETTINGS_PATH.parent.mkdir(parents=True, exist_ok=True)
                with open(GUI_SETTINGS_PATH, "w", encoding="utf-8") as f:
                    json.dump(settings, f)
            except Exception:
                pass

        self.api_key_var.trace_add("write", _save_settings)
        self.api_url_var.trace_add("write", _save_settings)
        self.api_model_var.trace_add("write", _save_settings)
        self.backend_var.trace_add("write", _save_settings)
        self.voice_profile_var.trace_add("write", _save_settings)
        
        def _on_backend_changed(*args):
            backend = self.backend_var.get()
            if backend == "OpenCode Zen API":
                self.api_url_var.set("https://opencode.ai/zen/v1")
            elif backend == "OpenAI API":
                if "opencode.ai" in self.api_url_var.get():
                    self.api_url_var.set("https://api.openai.com/v1")
                    
        self.backend_var.trace_add("write", _on_backend_changed)
        
        self.max_tokens_var = tk.StringVar(value=DEFAULT_MAX_NEW_TOKENS)
        self.temperature_var = tk.StringVar(value="0.7")
        self.top_k_var = tk.StringVar(value="20")
        self.status_var = tk.StringVar(
            value=f"CUDA available: {'yes' if torch.cuda.is_available() else 'no'} | Ready"
        )
        self.api_models_list: list[str] = ["gpt-4o-mini"]
        self.tts_enabled_var = tk.BooleanVar(value=True)
        self.remember_conversations_var = tk.BooleanVar(value=True)
        self.allow_web_search_var = tk.BooleanVar(value=False)
        pygame.mixer.init()
        self.vts_manager = VTSManager()
        threading.Thread(target=self.vts_manager.run, daemon=True).start()
        self.is_recording = False
        self.recognizer = sr.Recognizer()
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
        ttk.Label(backend, text="API Key").grid(row=0, column=2, padx=(12, 6))
        ttk.Entry(backend, textvariable=self.api_key_var, show="*", width=24).grid(row=0, column=3)
        
        ttk.Label(backend, text="API URL").grid(row=1, column=2, padx=(12, 6), pady=4)
        ttk.Entry(backend, textvariable=self.api_url_var, width=24).grid(row=1, column=3, pady=4)

        ttk.Label(backend, text="API Model").grid(row=2, column=2, padx=(12, 6), pady=4)
        
        self.api_models_list = ["gpt-4o-mini", "gpt-4o", "opencode-zen-2", "opencode-zen-1"]
        self.model_combo = ttk.Combobox(backend, textvariable=self.api_model_var, values=self.api_models_list, width=22)
        self.model_combo.grid(row=2, column=3, pady=4)
        
        ttk.Label(backend, text="Voice").grid(row=1, column=0, padx=(0, 6))
        ttk.Combobox(
            backend,
            textvariable=self.voice_profile_var,
            values=tuple(VOICE_PROFILE_OPTIONS),
            state="readonly",
            width=24,
        ).grid(row=1, column=1)
        
        ttk.Button(backend, text="Fetch", command=self._fetch_models, width=6).grid(row=2, column=4, padx=(4, 0))

        controls = ttk.Frame(model)
        controls.grid(row=3, column=0, columnspan=3, sticky="w", padx=6, pady=(2, 8))
        self._labeled_entry(controls, 0, "Max tokens", self.max_tokens_var, 8)
        self._labeled_entry(controls, 2, "Temperature", self.temperature_var, 8)
        self._labeled_entry(controls, 4, "Top-k", self.top_k_var, 8)

        privacy = ttk.LabelFrame(content, text="Privacy and memory")
        privacy.grid(row=1, column=0, sticky="ew", pady=(12, 0))
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
            row=2, column=0, sticky="e", pady=(14, 0)
        )

    def _fetch_models(self) -> None:
        api_key = self.api_key_var.get().strip()
        backend = self.backend_var.get()
        api_url = self.api_url_var.get().strip()
        
        if BACKEND_LABELS.get(backend) == BACKEND_OPENCODE:
            api_url = "https://opencode.ai/zen/v1"
            
        if not api_key:
            self.status_var.set("Please enter API Key first")
            return
            
        self.status_var.set("Fetching models...")
        self.root.update()
        
        def fetch_task():
            try:
                models = OpenAIRuntime.fetch_models(api_key, api_url)
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

    def _append_turn(self, turn: ChatTurn) -> None:
        self.transcript_text.configure(state="normal")
        if self._has_empty_state:
            self.transcript_text.delete("1.0", tk.END)
            self._has_empty_state = False
        for text, tag in conversation_display_segments(turn.prompt, turn.response):
            self.transcript_text.insert(tk.END, text, tag)
        self.transcript_text.see(tk.END)
        self.transcript_text.configure(state="disabled")

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

    def _memory_store_for_session(self) -> MemoryStore:
        if self.memory_store is None:
            self.memory_store = MemoryStore(self.memory_index_path, self.memory_texts_path)
        return self.memory_store

    def _clear_saved_memory(self) -> None:
        if not messagebox.askyesno(
            title="Clear saved memory",
            message="Delete locally saved conversation memory? This cannot be undone.",
            parent=self.root,
        ):
            return
        try:
            MemoryStore.clear_artifacts(self.memory_index_path, self.memory_texts_path)
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
                self.recognizer.adjust_for_ambient_noise(source, duration=0.5)
                audio = self.recognizer.listen(source, timeout=5, phrase_time_limit=15)
            self.root.after(0, lambda: self.status_var.set("Recognizing..."))
            
            # Using Google Web Speech API (free, no key required)
            text = self.recognizer.recognize_google(audio, language="vi-VN")
            
            self.root.after(0, lambda t=text: self._on_record_success(t))
        except sr.WaitTimeoutError:
            self.root.after(0, lambda: self._on_record_error("Listening timed out."))
        except sr.UnknownValueError:
            self.root.after(0, lambda: self._on_record_error("Could not understand audio."))
        except sr.RequestError as e:
            self.root.after(0, lambda: self._on_record_error(f"Speech API error: {e}"))
        except Exception as e:
            self.root.after(0, lambda: self._on_record_error(f"Error: {e}"))
            
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


    def _selected_voice_profile(self) -> str:
        return VOICE_PROFILE_OPTIONS.get(self.voice_profile_var.get(), DEFAULT_VOICE_PROFILE)


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
                past_memories = memory_store.search_memory(prompt, k=2)
                if past_memories:
                    memory_context = "\n---\n".join(past_memories)
            vts_catalog = self.vts_manager.current_catalog_sync() if hasattr(self, "vts_manager") else None

            with self.gpu_transition_lock:
                is_cloud = backend in (BACKEND_OPENAI, BACKEND_OPENCODE)
                uses_gwen = is_cloud and self.tts_enabled_var.get()
                if uses_gwen:
                    self.runner.release_gpu()
                    self.root.after(0, lambda: self.status_var.set("Warming Gwen voice..."))
                    ensure_gwen_tts_server()
                    request_gwen_control(
                        "/warmup", {"voice_profile": self._selected_voice_profile()}
                    )
                elif not is_cloud and gwen_tts_server_available():
                    request_gwen_control("/release")
                self.runner.select_backend(backend)
                cache_hit = self.runner.is_cached(checkpoint)
                self.root.after(
                    0,
                    lambda cache_hit=cache_hit: self.status_var.set(
                        "Ru is replying..." if cache_hit else "Loading Ru..."
                    ),
                )
                started = perf_counter()
                self.runner.load(checkpoint, self.api_key_var.get(), self.api_url_var.get(), model_name=self.api_model_var.get())
                result = self.runner.generate_reply(
                    prompt,
                    *settings,
                    turns_history=list(self.session_turns),
                    memory_context=memory_context,
                    allow_web_search=allow_web_search,
                    vts_instruction=catalog_instruction(vts_catalog),
                )
                elapsed = perf_counter() - started
            self.root.after(
                0,
                lambda result=result, cache_hit=cache_hit, elapsed=elapsed, prompt=prompt, remember_conversations=remember_conversations, vts_catalog=vts_catalog: self._generation_finished(
                    result,
                    cache_hit,
                    elapsed,
                    prompt,
                    remember_conversations,
                    vts_catalog,
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
    ) -> None:
        if not result.strip():
            self._generation_failed("model returned an empty response")
            return
        clean_result, catalog_codes = parse_catalog_reply(result, vts_catalog)
        vts_actions = requested_vts_actions(original_prompt)
        if not clean_result.strip():
            self._generation_failed("model returned no visible response")
            return
        turn = ChatTurn(original_prompt, clean_result)
        self.session_turns.append(turn)
        self.last_turn = turn
        self.last_turn_saved = False
        self._append_turn(turn)

        if (
            remember_conversations
            and self.remember_conversations_var.get()
            and self.memory_store
        ):
            self.memory_store.add_memory(f"User: {original_prompt}\nAssistant: {clean_result}")

        self.status_var.set(format_generation_status(cache_hit=cache_hit, seconds=elapsed))
        self._set_controls_enabled(True)
        self._update_mark_button()
        self.prompt_text.delete("1.0", tk.END)
        self.prompt_text.focus_set()

        if hasattr(self, "vts_manager"):
            if catalog_codes and hasattr(self.vts_manager, "trigger_catalog_actions_sync"):
                self.vts_manager.trigger_catalog_actions_sync(vts_catalog, catalog_codes)
            self.vts_manager.trigger_actions_sync(vts_actions)

        if self.tts_enabled_var.get():
            threading.Thread(target=self._play_tts, args=(clean_result,), daemon=True).start()

    def _play_tts(self, text: str) -> None:
        try:
            clean_text = clean_tts_text(text)
            if not clean_text:
                return

            self.root.after(0, lambda: self.status_var.set("Preparing voice..."))
            
            # Gwen replies are split so playback can begin before the full reply is synthesized.
            with self.gpu_transition_lock:
                self.runner.release_gpu()
                ensure_gwen_tts_server()
            self._play_gwen_chunks(
                split_tts_chunks(clean_text),
                fast_mode=True,
                voice_profile=self._selected_voice_profile(),
            )

        except Exception as error:
            self.root.after(0, lambda e=str(error): self.status_var.set(f"Voice unavailable: {e}"))
            if getattr(self, "voice_mode_active", False):
                self.root.after(1000, self._toggle_recording)

    def _play_gwen_chunks(
        self,
        chunks: list[str],
        *,
        fast_mode: bool,
        voice_profile: str = DEFAULT_VOICE_PROFILE,
    ) -> None:
        if not chunks:
            return

        generation = self._speech_generation
        files: queue.Queue[object] = queue.Queue(maxsize=1)
        stop_requested = threading.Event()
        end_of_queue = object()

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
            try:
                for chunk in chunks:
                    if stop_requested.is_set() or generation != self._speech_generation:
                        return
                    audio_file = request_tts_audio(
                        chunk, fast_mode=fast_mode, voice_profile=voice_profile
                    )
                    if not publish(audio_file):
                        remove_audio_file(audio_file)
                        return
            except Exception as error:
                publish(("error", str(error)))
            finally:
                publish(end_of_queue)

        if hasattr(self, "vts_manager"):
            self.vts_manager.is_speaking = True
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
                    self.root.after(0, lambda: self.status_var.set("Ru is speaking"))
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
                self.vts_manager.is_speaking = False
            if generation == self._speech_generation and getattr(self, "voice_mode_active", False):
                self.root.after(500, self._toggle_recording)

    def _generation_failed(self, message: str) -> None:
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
