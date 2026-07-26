from __future__ import annotations

import re
import sys
import threading
import tkinter as tk
from time import perf_counter
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

import tempfile
import os
import speech_recognition as sr
import asyncio
from .vts_manager import VTSManager
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
from .gui_presentation import PALETTE, format_generation_status
from .ru_style import RU_SYSTEM_PROMPT
from .memory import MemoryStore
from .search import perform_web_search

PROJECT_ROOT = Path(__file__).resolve().parents[2]
MODEL_NAME = "Ru"
GWEN_TTS_SERVER_URL = "http://127.0.0.1:5051/tts"
DEFAULT_MAX_NEW_TOKENS = "192"


def clean_tts_text(text: str) -> str:
    clean_text = re.sub(r"\[SEARCH:.*?\]", "", text).strip()
    clean_text = re.sub(r"<thought>.*?</thought>", "", clean_text, flags=re.DOTALL)
    clean_text = re.sub(r"<think>.*?</think>", "", clean_text, flags=re.DOTALL)
    return clean_text.replace("*", "").strip()


def request_tts_audio(text: str, *, post=None) -> str:
    if post is None:
        import requests

        post = requests.post
    try:
        response = post(GWEN_TTS_SERVER_URL, json={"text": text}, timeout=90)
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


def default_paths() -> Path:
    candidates = (
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

    def load(self, checkpoint_path: str | Path) -> str:
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
        return is_llama_generation_cached(self.base_model_path, checkpoint_path)

    def generate_reply(
        self,
        prompt: str,
        max_new_tokens: int,
        temperature: float,
        top_k: int,
        turns_history: list[ChatTurn] | None = None,
        memory_context: str | None = None,
        allow_web_search: bool = False,
    ) -> str:
        messages = build_conversation_messages(
            RU_SYSTEM_PROMPT,
            prompt,
            turns_history or (),
            memory_context=memory_context,
        )

        response = generate_llama_response_hf(
            str(self.base_model_path),
            tuple(messages),
            self.checkpoint_path,
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
            do_sample=True,
        )

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

            response = generate_llama_response_hf(
                str(self.base_model_path),
                tuple(messages),
                self.checkpoint_path,
                max_new_tokens=max_new_tokens,
                temperature=temperature,
                top_k=top_k,
                do_sample=True,
            )
        return response


class RuApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.runner = ModelRunner()
        self.worker: threading.Thread | None = None
        self.gpu_transition_lock = threading.Lock()
        self.session_turns: list[ChatTurn] = []
        self.last_turn: ChatTurn | None = None
        self.last_turn_saved = False
        self.conversation_store = ConversationStore(
            PROJECT_ROOT / "data" / "conversations" / "approved.jsonl"
        )
        self.memory_index_path = PROJECT_ROOT / "data" / "conversations" / "memory_index.faiss"
        self.memory_texts_path = PROJECT_ROOT / "data" / "conversations" / "memory_texts.json"
        self.memory_store: MemoryStore | None = None
        checkpoint = default_paths()
        self.checkpoint_var = tk.StringVar(value=str(checkpoint))
        self.max_tokens_var = tk.StringVar(value=DEFAULT_MAX_NEW_TOKENS)
        self.temperature_var = tk.StringVar(value="0.7")
        self.top_k_var = tk.StringVar(value="20")
        self.status_var = tk.StringVar(
            value=f"CUDA available: {'yes' if torch.cuda.is_available() else 'no'} | Ready"
        )
        self.tts_enabled_var = tk.BooleanVar(value=True)
        self.remember_conversations_var = tk.BooleanVar(value=False)
        self.allow_web_search_var = tk.BooleanVar(value=False)
        self.voice_name = "vi-VN-HoaiMyNeural" # Vietnamese female voice from edge-tts
        pygame.mixer.init()
        self.vts_manager = VTSManager()
        threading.Thread(target=lambda: asyncio.run(self.vts_manager.connect()), daemon=True).start()
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
        self.new_chat_button = ttk.Button(actions, text="New chat", command=self._new_chat, style="Ru.Quiet.TButton")
        self.new_chat_button.grid(row=0, column=2, padx=(0, 6))
        self.mark_button = ttk.Button(
            actions, text="Save useful reply", command=self._mark_last_response, state="disabled", style="Ru.Quiet.TButton"
        )
        self.mark_button.grid(row=0, column=3, padx=(0, 6))
        self.tts_checkbox = ttk.Checkbutton(actions, text="Ru voice", variable=self.tts_enabled_var)
        self.tts_checkbox.grid(row=0, column=4, padx=(6, 0))
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
        controls = ttk.Frame(model)
        controls.grid(row=1, column=0, columnspan=3, sticky="w", padx=6, pady=(2, 8))
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
        self.transcript_text.insert(
            tk.END,
            f"{MODEL_NAME}\n",
            "ru_label",
        )
        self.transcript_text.insert(tk.END, f"{turn.response}\n\n", "ru_bubble")
        self.transcript_text.insert(tk.END, "You\n", "user_label")
        self.transcript_text.insert(tk.END, f"{turn.prompt}\n\n", "user_bubble")
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

    def _on_record_error(self, msg: str) -> None:
        self.is_recording = False
        self.speak_button.configure(state="normal", text="Voice input")
        self.status_var.set(msg)

    def _submit_from_keyboard(self, _event: object) -> str:
        self._start_generation()
        return "break"


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
        except ValueError as error:
            self.status_var.set(str(error))
            return

        self._set_controls_enabled(False)
        self.status_var.set("Waiting for Ru...")
        checkpoint = self.checkpoint_var.get()
        remember_conversations = self.remember_conversations_var.get()
        allow_web_search = self.allow_web_search_var.get()
        self.worker = threading.Thread(
            target=self._generate_worker,
            args=(prompt, settings, checkpoint, remember_conversations, allow_web_search),
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

            with self.gpu_transition_lock:
                cache_hit = self.runner.is_cached(checkpoint)
                self.root.after(
                    0,
                    lambda cache_hit=cache_hit: self.status_var.set(
                        "Ru is replying..." if cache_hit else "Loading Ru..."
                    ),
                )
                started = perf_counter()
                self.runner.load(checkpoint)
                result = self.runner.generate_reply(
                    prompt,
                    *settings,
                    turns_history=list(self.session_turns),
                    memory_context=memory_context,
                    allow_web_search=allow_web_search,
                )
                elapsed = perf_counter() - started
            self.root.after(
                0,
                lambda result=result, cache_hit=cache_hit, elapsed=elapsed, prompt=prompt, remember_conversations=remember_conversations: self._generation_finished(
                    result,
                    cache_hit,
                    elapsed,
                    prompt,
                    remember_conversations,
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
    ) -> None:
        if not result.strip():
            self._generation_failed("model returned an empty response")
            return
        turn = ChatTurn(original_prompt, result)
        self.session_turns.append(turn)
        self.last_turn = turn
        self.last_turn_saved = False
        self._append_turn(turn)

        if (
            remember_conversations
            and self.remember_conversations_var.get()
            and self.memory_store
        ):
            self.memory_store.add_memory(f"User: {original_prompt}\nAssistant: {result}")

        self.status_var.set(format_generation_status(cache_hit=cache_hit, seconds=elapsed))
        self._set_controls_enabled(True)
        self._update_mark_button()
        self.prompt_text.delete("1.0", tk.END)
        self.prompt_text.focus_set()

        emotions = re.findall(r"\*(.*?)\*", result)
        if emotions and hasattr(self, "vts_manager"):
            for emotion in emotions:
                self.vts_manager.trigger_hotkey_sync(emotion)

        if self.tts_enabled_var.get():
            threading.Thread(target=self._play_tts, args=(result,), daemon=True).start()

    def _play_tts(self, text: str) -> None:
        try:
            clean_text = clean_tts_text(text)
            if not clean_text:
                return

            self.root.after(0, lambda: self.status_var.set("Preparing voice..."))
            with self.gpu_transition_lock:
                clear_llama_generation_cache()
                audio_file = request_tts_audio(clean_text)

            if pygame.mixer.music.get_busy():
                pygame.mixer.music.stop()
                pygame.mixer.music.unload()

            pygame.mixer.music.load(audio_file)
            pygame.mixer.music.play()
            self.root.after(0, lambda: self.status_var.set("Ru is speaking"))

            def wait_and_clean() -> None:
                while pygame.mixer.music.get_busy():
                    pygame.time.Clock().tick(10)
                pygame.mixer.music.unload()
                try:
                    os.remove(audio_file)
                except OSError:
                    pass

            threading.Thread(target=wait_and_clean, daemon=True).start()

        except Exception as error:
            self.root.after(0, lambda: self.status_var.set(f"Voice unavailable: {error}"))


    def _generation_failed(self, message: str) -> None:
        self.status_var.set(f"Error: {message}")
        self._set_controls_enabled(True)
        self._update_mark_button()


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    root = tk.Tk()
    RuApp(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
