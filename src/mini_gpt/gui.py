from __future__ import annotations

import re
import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, ttk

import tempfile
import os
import speech_recognition as sr
import asyncio
from .vts_manager import VTSManager
import pygame

import torch

from .chat import ChatTurn
from .conversation_store import ConversationStore
from .llama3_1_runtime import generate_llama_response_hf
from .ru_style import RU_SYSTEM_PROMPT
from .memory import MemoryStore
from .search import perform_web_search

PROJECT_ROOT = Path(__file__).resolve().parents[2]
MODEL_NAME = "Ru"


def default_paths() -> Path:
    candidates = (
        PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v6_2",
        PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v6_3",
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
        adapter_dir = Path(checkpoint_path)
        if not adapter_dir.is_dir():
            raise FileNotFoundError(f"adapter directory not found: {adapter_dir}")
        self.checkpoint_path = adapter_dir
        return f"Ready (Llama 3.1 8B QLoRA) | {adapter_dir.name}"

    def generate_reply(
        self,
        prompt: str,
        max_new_tokens: int,
        temperature: float,
        top_k: int,
        turns_history: list[ChatTurn] | None = None,
        memory_context: str | None = None,
    ) -> str:
        messages: list[tuple[str, str]] = [("system", RU_SYSTEM_PROMPT)]
        if memory_context:
            messages.append(("system", f"Past memories:\n{memory_context}\n\nUse this to answer if relevant."))
        if turns_history:
            for turn in turns_history:
                messages.append(("user", turn.prompt))
                messages.append(("assistant", turn.response))
        messages.append(("user", prompt))

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
        if search_match:
            query = search_match.group(1).strip()
            search_results = perform_web_search(query)
            last_user_msg = messages.pop()
            messages.append(("user", f"{last_user_msg[1]}\n\n[System note: Web search results for '{query}':\n{search_results}]"))

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


class MiniGPTApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.runner = ModelRunner()
        self.worker: threading.Thread | None = None
        self.session_turns: list[ChatTurn] = []
        self.last_turn: ChatTurn | None = None
        self.last_turn_saved = False
        self.conversation_store = ConversationStore(
            PROJECT_ROOT / "data" / "conversations" / "approved.jsonl"
        )
        self.memory_store = MemoryStore(
            PROJECT_ROOT / "data" / "conversations" / "memory_index.faiss",
            PROJECT_ROOT / "data" / "conversations" / "memory_texts.json"
        )
        checkpoint = default_paths()
        self.checkpoint_var = tk.StringVar(value=str(checkpoint))
        self.max_tokens_var = tk.StringVar(value="512")
        self.temperature_var = tk.StringVar(value="0.7")
        self.top_k_var = tk.StringVar(value="20")
        self.status_var = tk.StringVar(
            value=f"CUDA available: {'yes' if torch.cuda.is_available() else 'no'} | Ready"
        )
        self.tts_enabled_var = tk.BooleanVar(value=True)
        self.voice_name = "vi-VN-HoaiMyNeural" # Vietnamese female voice from edge-tts
        pygame.mixer.init()
        self.vts_manager = VTSManager()
        threading.Thread(target=lambda: asyncio.run(self.vts_manager.connect()), daemon=True).start()
        self.is_recording = False
        self.recognizer = sr.Recognizer()
        self.generate_button: ttk.Button
        self.new_chat_button: ttk.Button
        self.mark_button: ttk.Button
        self.prompt_text: tk.Text
        self.transcript_text: tk.Text
        self._build()

    def _build(self) -> None:
        self.root.title(f"{MODEL_NAME} — Llama 3.1 GUI")
        self.root.geometry("900x720")
        self.root.minsize(700, 520)
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(1, weight=1)

        settings = ttk.LabelFrame(self.root, text="Model Settings")
        settings.grid(row=0, column=0, sticky="ew", padx=12, pady=(12, 6))
        settings.columnconfigure(1, weight=1)
        self._path_row(settings, 0, "Checkpoint/Adapter", self.checkpoint_var, self._browse_checkpoint)

        controls = ttk.Frame(settings)
        controls.grid(row=1, column=0, columnspan=3, sticky="w", padx=6, pady=6)
        self._labeled_entry(controls, 0, "Max tokens", self.max_tokens_var, 70)
        self._labeled_entry(controls, 2, "Temperature", self.temperature_var, 70)
        self._labeled_entry(controls, 4, "Top-k", self.top_k_var, 70)

        content = ttk.Frame(self.root)
        content.grid(row=1, column=0, sticky="nsew", padx=12, pady=6)
        content.columnconfigure(0, weight=1)
        content.rowconfigure(1, weight=3)
        content.rowconfigure(3, weight=1)

        ttk.Label(content, text=f"Conversation with {MODEL_NAME}").grid(row=0, column=0, sticky="w")
        transcript_frame = ttk.Frame(content)
        transcript_frame.grid(row=1, column=0, sticky="nsew", pady=(4, 8))
        transcript_frame.columnconfigure(0, weight=1)
        transcript_frame.rowconfigure(0, weight=1)
        self.transcript_text = tk.Text(transcript_frame, wrap="word", state="disabled")
        self.transcript_text.grid(row=0, column=0, sticky="nsew")
        transcript_scrollbar = ttk.Scrollbar(
            transcript_frame,
            orient="vertical",
            command=self.transcript_text.yview,
        )
        transcript_scrollbar.grid(row=0, column=1, sticky="ns")
        self.transcript_text.configure(yscrollcommand=transcript_scrollbar.set)

        ttk.Label(content, text="Message").grid(row=2, column=0, sticky="w")
        self.prompt_text = tk.Text(content, height=4, wrap="word")
        self.prompt_text.grid(row=3, column=0, sticky="nsew", pady=(4, 8))

        actions = ttk.Frame(content)
        actions.grid(row=4, column=0, sticky="w", pady=(0, 8))
        self.generate_button = ttk.Button(actions, text="Send", command=self._start_generation)
        self.generate_button.grid(row=0, column=0, padx=(0, 6))
        
        self.speak_button = ttk.Button(actions, text="🎤 Speak", command=self._toggle_recording)
        self.speak_button.grid(row=0, column=1, padx=(0, 6))
        
        self.new_chat_button = ttk.Button(actions, text="New chat", command=self._new_chat)
        self.new_chat_button.grid(row=0, column=2, padx=(0, 6))
        
        self.mark_button = ttk.Button(
            actions,
            text="Mark last response useful",
            command=self._mark_last_response,
            state="disabled",
        )
        self.mark_button.grid(row=0, column=3, padx=(0, 6))
        
        self.tts_checkbox = ttk.Checkbutton(actions, text="Ru Voice", variable=self.tts_enabled_var)
        self.tts_checkbox.grid(row=0, column=4, padx=(0, 6))
        
        ttk.Label(content, textvariable=self.status_var).grid(row=5, column=0, sticky="w", pady=(0, 4))

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
        self.transcript_text.insert(
            tk.END,
            f"User:\n{turn.prompt}\n\n{MODEL_NAME}:\n{turn.response}\n\n",
        )
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
        self._update_mark_button()
        self.status_var.set("New chat started")

    def _update_mark_button(self) -> None:
        state = "normal" if self.last_turn is not None and not self.last_turn_saved else "disabled"
        self.mark_button.configure(state=state)

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
            return  # Already recording, let it finish or wait
        self.is_recording = True
        self.speak_button.configure(state="disabled", text="🎤 Listening...")
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
        self.speak_button.configure(state="normal", text="🎤 Speak")
        self.status_var.set(f"Heard: {text}")
        current_text = self.prompt_text.get("1.0", tk.END).strip()
        new_text = f"{current_text} {text}".strip() if current_text else text
        self.prompt_text.delete("1.0", tk.END)
        self.prompt_text.insert(tk.END, new_text)
        
    def _on_record_error(self, msg: str) -> None:
        self.is_recording = False
        self.speak_button.configure(state="normal", text="🎤 Speak")
        self.status_var.set(msg)

    def _start_generation(self) -> None:
        if self.worker is not None and self.worker.is_alive():
            return
        prompt = self.prompt_text.get("1.0", tk.END).strip()
        if not prompt:
            self.status_var.set("Enter a prompt first")
            return
        try:
            settings = parse_generation_settings(
                self.max_tokens_var.get(), self.temperature_var.get(), self.top_k_var.get()
            )
        except ValueError as error:
            self.status_var.set(str(error))
            return

        self.generate_button.configure(state="disabled")
        self.speak_button.configure(state="disabled")
        self.new_chat_button.configure(state="disabled")
        self.mark_button.configure(state="disabled")
        self.status_var.set("Loading model / generating...")
        checkpoint = self.checkpoint_var.get()
        self.worker = threading.Thread(
            target=self._generate_worker,
            args=(prompt, settings, checkpoint),
            daemon=True,
        )
        self.worker.start()

    def _generate_worker(
        self,
        prompt: str,
        settings: tuple[int, float, int],
        checkpoint: str,
    ) -> None:
        try:
            status = self.runner.load(checkpoint)
            
            memory_context = None
            if self.memory_store:
                past_memories = self.memory_store.search_memory(prompt, k=2)
                if past_memories:
                    memory_context = "\n---\n".join(past_memories)

            result = self.runner.generate_reply(
                prompt,
                *settings,
                turns_history=list(self.session_turns),
                memory_context=memory_context,
            )
            self.root.after(
                0,
                lambda result=result, status=status, prompt=prompt: self._generation_finished(
                    result,
                    status,
                    prompt,
                ),
            )
        except Exception as error:
            import traceback
            traceback.print_exc()
            message = str(error) if str(error) else repr(error)
            self.root.after(0, lambda message=message: self._generation_failed(message))

    def _generation_finished(self, result: str, status: str, original_prompt: str) -> None:
        if not result.strip():
            self._generation_failed("model returned an empty response")
            return
        turn = ChatTurn(original_prompt, result)
        self.session_turns.append(turn)
        self.last_turn = turn
        self.last_turn_saved = False
        self._append_turn(turn)

        if hasattr(self, 'memory_store') and self.memory_store:
            self.memory_store.add_memory(f"User: {original_prompt}\nAssistant: {result}")
            
        self.status_var.set(status)
        self.generate_button.configure(state="normal")
        self.speak_button.configure(state="normal")
        self.new_chat_button.configure(state="normal")
        self._update_mark_button()
        self.prompt_text.delete("1.0", tk.END)
        self.prompt_text.focus_set()
        
        # Trigger VTS expressions if any *emotion* found
        import re
        emotions = re.findall(r"\*(.*?)\*", result)
        if emotions and hasattr(self, 'vts_manager'):
            for emotion in emotions:
                self.vts_manager.trigger_hotkey_sync(emotion)

        if self.tts_enabled_var.get():
            threading.Thread(target=self._play_tts, args=(result,), daemon=True).start()

    def _play_tts(self, text: str) -> None:
        try:
            # We strip out [SEARCH: ...] tags from spoken text
            clean_text = re.sub(r"\[SEARCH:.*?\]", "", text).strip()
            # Also strip thought blocks if any (between <thought> or <think> tags)
            clean_text = re.sub(r"<thought>.*?</thought>", "", clean_text, flags=re.DOTALL)
            clean_text = re.sub(r"<think>.*?</think>", "", clean_text, flags=re.DOTALL)
            clean_text = clean_text.replace("*", "") # Remove markdown asterisks
            if not clean_text:
                return

            audio_file = None
            try:
                import requests
                resp = requests.post("http://127.0.0.1:5050/tts", json={"text": clean_text}, timeout=60)
                if resp.status_code == 200:
                    audio_file = resp.json().get("audio_path")
                else:
                    print(f"F5-TTS server error: {resp.text}")
            except Exception as e:
                print(f"F5-TTS local server not running or failed: {e}")
            
            if not audio_file:
                print("Skipping TTS because F5-TTS server is not available.")
                return
            
            # Stop any playing audio before playing new one
            if pygame.mixer.music.get_busy():
                pygame.mixer.music.stop()
                pygame.mixer.music.unload()

            pygame.mixer.music.load(audio_file)
            pygame.mixer.music.play()
            
            # Wait for playback to finish to clean up file
            def wait_and_clean():
                while pygame.mixer.music.get_busy():
                    pygame.time.Clock().tick(10)
                pygame.mixer.music.unload()
                try:
                    os.remove(audio_file)
                except Exception:
                    pass

            threading.Thread(target=wait_and_clean, daemon=True).start()

        except Exception as e:
            self.root.after(0, lambda: self.status_var.set(f"TTS Error: {e}"))

    def _generation_failed(self, message: str) -> None:
        self.status_var.set(f"Error: {message}")
        self.generate_button.configure(state="normal")
        self.speak_button.configure(state="normal")
        self.new_chat_button.configure(state="normal")
        self._update_mark_button()


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    root = tk.Tk()
    MiniGPTApp(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
