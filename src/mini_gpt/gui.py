from __future__ import annotations

import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, ttk
from typing import Any

import torch

from .chat import ChatTurn, format_chat_prompt
from .checkpointing import load_checkpoint
from .config import ModelConfig
from .conversation_store import ConversationStore
from .evaluate import generate, generate_completion
from .model import MiniGPT
from .tokenizer import load_tokenizer
from .train import resolve_device

PROJECT_ROOT = Path(__file__).resolve().parents[2]
MODEL_NAME = "Ru"


def default_paths() -> tuple[Path, Path]:
    checkpoint_candidates = (
        PROJECT_ROOT / "checkpoints" / "chat-finetune" / "latest.pt",
        PROJECT_ROOT / "checkpoints" / "base" / "latest.pt",
        PROJECT_ROOT / "checkpoints" / "base-smoke" / "latest.pt",
        PROJECT_ROOT / "checkpoints" / "latest.pt",
    )
    checkpoint = next((path for path in checkpoint_candidates if path.is_file()), checkpoint_candidates[-1])
    return checkpoint, PROJECT_ROOT / "data" / "tokenizer.json"


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


def _load_payload(path: Path, device: torch.device) -> dict[str, Any]:
    try:
        return torch.load(path, map_location=device, weights_only=False)
    except TypeError:
        return torch.load(path, map_location=device)


class ModelRunner:
    def __init__(self) -> None:
        self.model: MiniGPT | None = None
        self.tokenizer: Any | None = None
        self.device: torch.device | None = None
        self.checkpoint_path: Path | None = None
        self.tokenizer_path: Path | None = None

    def load(self, checkpoint_path: str | Path, tokenizer_path: str | Path, device_name: str = "auto") -> str:
        checkpoint = Path(checkpoint_path)
        tokenizer_file = Path(tokenizer_path)
        if not checkpoint.is_file():
            raise FileNotFoundError(f"checkpoint not found: {checkpoint}")
        if not tokenizer_file.is_file():
            raise FileNotFoundError(f"tokenizer not found: {tokenizer_file}")
        device = resolve_device(device_name)
        if (
            self.model is not None
            and self.device == device
            and self.checkpoint_path == checkpoint
            and self.tokenizer_path == tokenizer_file
        ):
            return f"Ready | {device} | {self.model.parameter_count():,} parameters"
        payload = _load_payload(checkpoint, device)
        model_config = ModelConfig.from_dict(payload["model_config"])
        model = MiniGPT(model_config).to(device)
        load_checkpoint(checkpoint, model, device=device, restore_rng=False)
        model.eval()
        self.model = model
        self.tokenizer = load_tokenizer(tokenizer_file)
        self.device = device
        self.checkpoint_path = checkpoint
        self.tokenizer_path = tokenizer_file
        return f"Ready | {device} | {model.parameter_count():,} parameters"

    def generate_text(
        self,
        prompt: str,
        max_new_tokens: int,
        temperature: float,
        top_k: int,
    ) -> str:
        if self.model is None or self.tokenizer is None or self.device is None:
            raise RuntimeError("load a checkpoint before generating")
        return generate(
            self.model,
            self.tokenizer,
            prompt,
            self.device,
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
        )

    def generate_reply(
        self,
        prompt: str,
        max_new_tokens: int,
        temperature: float,
        top_k: int,
    ) -> str:
        if self.model is None or self.tokenizer is None or self.device is None:
            raise RuntimeError("load a checkpoint before generating")
        return generate_completion(
            self.model,
            self.tokenizer,
            prompt,
            self.device,
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
        )


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
        checkpoint, tokenizer = default_paths()
        self.checkpoint_var = tk.StringVar(value=str(checkpoint))
        self.tokenizer_var = tk.StringVar(value=str(tokenizer))
        self.max_tokens_var = tk.StringVar(value="50")
        self.temperature_var = tk.StringVar(value="0.8")
        self.top_k_var = tk.StringVar(value="20")
        self.status_var = tk.StringVar(
            value=f"CUDA available: {'yes' if torch.cuda.is_available() else 'no'} | Ready"
        )
        self.generate_button: ttk.Button
        self.new_chat_button: ttk.Button
        self.mark_button: ttk.Button
        self.prompt_text: tk.Text
        self.transcript_text: tk.Text
        self._build()

    def _build(self) -> None:
        self.root.title(f"{MODEL_NAME} — Bilingual Mini-GPT")
        self.root.geometry("900x700")
        self.root.minsize(700, 520)
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(1, weight=1)

        settings = ttk.LabelFrame(self.root, text="Model")
        settings.grid(row=0, column=0, sticky="ew", padx=12, pady=(12, 6))
        settings.columnconfigure(1, weight=1)
        self._path_row(settings, 0, "Checkpoint", self.checkpoint_var, self._browse_checkpoint)
        self._path_row(settings, 1, "Tokenizer", self.tokenizer_var, self._browse_tokenizer)

        controls = ttk.Frame(settings)
        controls.grid(row=2, column=0, columnspan=3, sticky="w", padx=6, pady=6)
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
        self.new_chat_button = ttk.Button(actions, text="New chat", command=self._new_chat)
        self.new_chat_button.grid(row=0, column=1, padx=(0, 6))
        self.mark_button = ttk.Button(
            actions,
            text="Mark last response useful",
            command=self._mark_last_response,
            state="disabled",
        )
        self.mark_button.grid(row=0, column=2)
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
        selected = filedialog.askopenfilename(filetypes=[("PyTorch checkpoint", "*.pt"), ("All files", "*.*")])
        if selected:
            self.checkpoint_var.set(selected)

    def _browse_tokenizer(self) -> None:
        selected = filedialog.askopenfilename(filetypes=[("Tokenizer JSON", "*.json"), ("All files", "*.*")])
        if selected:
            self.tokenizer_var.set(selected)

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
        context_prompt = format_chat_prompt(self.session_turns, prompt)
        self.generate_button.configure(state="disabled")
        self.new_chat_button.configure(state="disabled")
        self.mark_button.configure(state="disabled")
        self.status_var.set("Loading model / generating...")
        checkpoint = self.checkpoint_var.get()
        tokenizer = self.tokenizer_var.get()
        self.worker = threading.Thread(
            target=self._generate_worker,
            args=(context_prompt, prompt, settings, checkpoint, tokenizer),
            daemon=True,
        )
        self.worker.start()

    def _generate_worker(
        self,
        context_prompt: str,
        original_prompt: str,
        settings: tuple[int, float, int],
        checkpoint: str,
        tokenizer: str,
    ) -> None:
        try:
            status = self.runner.load(checkpoint, tokenizer)
            result = self.runner.generate_reply(context_prompt, *settings)
            self.root.after(
                0,
                lambda result=result, status=status, original_prompt=original_prompt: self._generation_finished(
                    result,
                    status,
                    original_prompt,
                ),
            )
        except Exception as error:  # UI boundary: show any inference failure without killing the window.
            message = str(error)
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
        self.status_var.set(status)
        self.generate_button.configure(state="normal")
        self.new_chat_button.configure(state="normal")
        self._update_mark_button()
        self.prompt_text.delete("1.0", tk.END)
        self.prompt_text.focus_set()

    def _generation_failed(self, message: str) -> None:
        self.status_var.set(f"Error: {message}")
        self.generate_button.configure(state="normal")
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
