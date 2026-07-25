from __future__ import annotations

import json
import random
import subprocess
import threading
from dataclasses import dataclass, asdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Literal
import torch

from ._text_utils import normalize_text

IGNORE_LABEL = -100

@dataclass(frozen=True)
class LlamaMessage:
    role: Literal["system", "user", "assistant"]
    content: str

    def __post_init__(self) -> None:
        if self.role not in {"system", "user", "assistant"}:
            raise ValueError("Role must be system, user, or assistant")
        if not normalize_text(self.content):
            raise ValueError("Message content cannot be empty")

@dataclass(frozen=True)
class EncodedLlamaResponse:
    input_ids: list[int]
    labels: list[int]


def read_nvidia_smi_memory_mib() -> float | None:
    try:
        completed = subprocess.run(
            [
                "nvidia-smi",
                "--query-gpu=memory.used",
                "--format=csv,noheader,nounits",
            ],
            check=True,
            capture_output=True,
            text=True,
            timeout=2,
        )
    except (OSError, subprocess.SubprocessError):
        return None

    values: list[float] = []
    for line in completed.stdout.splitlines():
        stripped = line.strip()
        if not stripped:
            continue
        try:
            values.append(float(stripped))
        except ValueError:
            return None
    return values[0] if values else None


class PhysicalVramMonitor:
    def __init__(self, cap_mib: float, poll_seconds: float) -> None:
        if cap_mib <= 0:
            raise ValueError("physical VRAM cap must be positive")
        if poll_seconds <= 0:
            raise ValueError("physical VRAM poll interval must be positive")
        self.cap_mib = cap_mib
        self.poll_seconds = poll_seconds
        self._stop = threading.Event()
        self._exceeded = threading.Event()
        self._lock = threading.Lock()
        self._peak_mib: float | None = None
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        if self._thread is not None and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._poll, daemon=True, name="ru-vram-monitor")
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=max(2.0, self.poll_seconds * 4))

    def _poll(self) -> None:
        while not self._stop.is_set():
            current = read_nvidia_smi_memory_mib()
            if current is not None:
                with self._lock:
                    self._peak_mib = max(self._peak_mib or current, current)
                if current > self.cap_mib:
                    self._exceeded.set()
            self._stop.wait(self.poll_seconds)

    @property
    def peak_mib(self) -> float | None:
        with self._lock:
            return self._peak_mib

    @property
    def exceeded(self) -> bool:
        return self._exceeded.is_set()


def validate_v7_1_adapter_dir(path: str | Path) -> Path:
    adapter_dir = Path(path)
    if adapter_dir.name in {
        "ru-llama3.1-8b-instruct-v6",
        "ru-llama3.1-8b-instruct-v7",
    }:
        raise ValueError("v7.1 output cannot overwrite a comparison adapter")
    if adapter_dir.name != "ru-llama3.1-8b-instruct-v7.1":
        raise ValueError("v7.1 output must use the ru-llama3.1-8b-instruct-v7.1 adapter directory")
    return adapter_dir


_CHECKPOINT_ROOT = (Path(__file__).resolve().parents[2] / "checkpoints").resolve()
_V8_OUTPUT_NAMES = frozenset(
    {
        "ru-llama3.1-8b-instruct-v8",
        "ru-llama3.1-8b-instruct-v8-smoke",
    }
)
_V8_SOURCE_NAME = "ru-llama3.1-8b-instruct-v6"
_V6_1_OUTPUT_NAMES = frozenset(
    {
        "ru-llama3.1-8b-instruct-v6.1",
        "ru-llama3.1-8b-instruct-v6.1-smoke",
    }
)
_V6_1_SOURCE_NAME = "ru-llama3.1-8b-instruct-v6"


def validate_v8_adapter_dir(path: str | Path) -> Path:
    adapter_dir = Path(path)
    if adapter_dir.name not in _V8_OUTPUT_NAMES:
        raise ValueError(
            "v8 output must use ru-llama3.1-8b-instruct-v8 or "
            "ru-llama3.1-8b-instruct-v8-smoke"
        )
    if adapter_dir.resolve().parent != _CHECKPOINT_ROOT:
        raise ValueError("v8 output must be directly inside the D:/LLM/checkpoints directory")
    return adapter_dir


def validate_v8_source_adapter_dir(path: str | Path) -> Path:
    source_dir = Path(path)
    if source_dir.name != _V8_SOURCE_NAME or source_dir.resolve().parent != _CHECKPOINT_ROOT:
        raise ValueError("v8 source adapter must be the v6 adapter inside D:/LLM/checkpoints")
    return source_dir


def validate_v6_1_adapter_dir(path: str | Path) -> Path:
    adapter_dir = Path(path)
    if adapter_dir.name not in _V6_1_OUTPUT_NAMES:
        raise ValueError(
            "v6.1 output must use ru-llama3.1-8b-instruct-v6.1 or "
            "ru-llama3.1-8b-instruct-v6.1-smoke"
        )
    if adapter_dir.resolve().parent != _CHECKPOINT_ROOT:
        raise ValueError("v6.1 output must be directly inside the D:/LLM/checkpoints directory")
    return adapter_dir


def validate_v6_1_source_adapter_dir(path: str | Path) -> Path:
    source_dir = Path(path)
    if source_dir.name != _V6_1_SOURCE_NAME or source_dir.resolve().parent != _CHECKPOINT_ROOT:
        raise ValueError("v6.1 source adapter must be the v6 adapter inside D:/LLM/checkpoints")
    return source_dir

class LlamaResponseCollator:
    def __init__(self, pad_token_id: int) -> None:
        self.pad_token_id = pad_token_id

    def __call__(self, examples: list[EncodedLlamaResponse]) -> dict[str, torch.Tensor]:
        if not examples:
            raise ValueError("Batch cannot be empty")
        max_length = max(len(example.input_ids) for example in examples)
        input_rows: list[list[int]] = []
        attention_rows: list[list[int]] = []
        label_rows: list[list[int]] = []
        for example in examples:
            if len(example.input_ids) != len(example.labels):
                raise ValueError("Inputs and labels must have equal lengths")
            padding = max_length - len(example.input_ids)
            input_rows.append([self.pad_token_id] * padding + example.input_ids)
            attention_rows.append([0] * padding + [1] * len(example.input_ids))
            label_rows.append([IGNORE_LABEL] * padding + example.labels)
        return {
            "input_ids": torch.tensor(input_rows, dtype=torch.long),
            "attention_mask": torch.tensor(attention_rows, dtype=torch.long),
            "labels": torch.tensor(label_rows, dtype=torch.long),
        }

def encode_llama_response(
    tokenizer: Any,
    messages: tuple[LlamaMessage, ...],
    response: str,
    max_length: int,
) -> EncodedLlamaResponse:
    if max_length <= 0:
        raise ValueError("max_length must be positive")
    if not messages or messages[-1].role != "user":
        raise ValueError("Context must end with a user message")
    response = normalize_text(response)
    if not response:
        raise ValueError("Response cannot be empty")

    prompt_ids = tokenizer.apply_chat_template(
        [asdict(message) for message in messages],
        tokenize=True,
        add_generation_prompt=True,
    )
    
    # Llama 3 specific: response ends with <|eot_id|>
    eot_token_id = tokenizer.convert_tokens_to_ids("<|eot_id|>")
    if eot_token_id is None:
        eot_token_id = tokenizer.eos_token_id
        
    response_ids = [*tokenizer(response, add_special_tokens=False)["input_ids"], eot_token_id]
    if len(response_ids) >= max_length:
        raise ValueError("assistant response does not fit within max_length")
    prompt_ids = prompt_ids[-(max_length - len(response_ids)) :]
    return EncodedLlamaResponse(
        input_ids=[*prompt_ids, *response_ids],
        labels=[IGNORE_LABEL] * len(prompt_ids) + response_ids,
    )

@dataclass(frozen=True)
class LlamaTrainingConfig:
    base_model_id: str
    base_model_path: str
    source_adapter_dir: str | None = None
    dataset_dir: str = "data/conversations/ru_v3"
    adapter_dir: str = "checkpoints/ru-llama3.1-8b-instruct"
    max_length: int = 512
    seed: int = 42
    epochs: int = 1
    learning_rate: float = 5e-5
    batch_size: int = 1
    gradient_accumulation_steps: int = 8
    evaluation_steps: int = 4
    save_steps: int = 4
    logging_steps: int = 1
    lora_rank: int = 8
    lora_alpha: int = 16
    lora_dropout: float = 0.05
    target_modules: tuple[str, ...] = ("q_proj", "k_proj", "v_proj", "o_proj")
    backend: str = "llama3.1-qlora"
    profile_name: str = "ru_llama3_1"
    physical_vram_cap_mib: float | None = None
    physical_vram_poll_seconds: float = 0.5

    @classmethod
    def from_json(cls, path: str | Path) -> "LlamaTrainingConfig":
        payload = json.loads(Path(path).read_text(encoding="utf-8"))
        if not isinstance(payload, dict):
            raise ValueError("Training config must be an object")
        if "target_modules" in payload:
            payload["target_modules"] = tuple(payload["target_modules"])
        return cls(**payload)

def write_adapter_manifest(
    adapter_dir: str | Path,
    *,
    backend: str,
    profile_name: str,
    base_model_id: str,
    base_model_path: str,
    source_adapter_dir: str | None = None,
    dataset_sha256: str,
    best_validation_loss: float,
    peak_vram_mb: float,
    seed: int,
    max_length: int,
    optimizer_updates: int = 0,
    physical_peak_mib: float | None = None,
    physical_cap_mib: float | None = None,
    physical_cap_passed: bool | None = None,
) -> dict[str, object]:
    destination = Path(adapter_dir)
    manifest = {
        "adapter_dir": str(destination),
        "backend": backend,
        "profile_name": profile_name,
        "base_model_id": base_model_id,
        "base_model_path": base_model_path,
        "source_adapter_dir": source_adapter_dir,
        "dataset_sha256": dataset_sha256,
        "best_validation_loss": best_validation_loss,
        "peak_vram_mb": peak_vram_mb,
        "physical_peak_mib": physical_peak_mib,
        "physical_cap_mib": physical_cap_mib,
        "physical_cap_passed": physical_cap_passed,
        "seed": seed,
        "max_length": max_length,
        "optimizer_updates": optimizer_updates,
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
    }
    destination.mkdir(parents=True, exist_ok=True)
    (destination / "ru_manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest

def train_ru_adapter_llama(config: LlamaTrainingConfig, max_steps: int | None = None) -> dict[str, object]:
    from hashlib import sha256
    import torch
    from peft import LoraConfig, PeftModel, get_peft_model, prepare_model_for_kbit_training
    from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig

    if not torch.cuda.is_available():
        raise RuntimeError("QLoRA training requires CUDA")
    base_path = Path(config.base_model_path)
    if not base_path.is_dir():
        raise FileNotFoundError(f"Model path {base_path} not found")
        
    dataset_dir = Path(config.dataset_dir)
    train_path, validation_path, manifest_path = dataset_dir / "train.jsonl", dataset_dir / "val.jsonl", dataset_dir / "manifest.json"
    if not train_path.is_file() or not validation_path.is_file() or not manifest_path.is_file():
        raise FileNotFoundError("Ru training, validation, or manifest data is missing")
    dataset_sha256 = sha256(manifest_path.read_bytes()).hexdigest()
    
    tokenizer = AutoTokenizer.from_pretrained(base_path)
    if tokenizer.pad_token_id is None:
        tokenizer.pad_token_id = tokenizer.convert_tokens_to_ids("<|finetune_right_pad_id|>") or tokenizer.eos_token_id
        
    quantization = BitsAndBytesConfig(load_in_4bit=True, bnb_4bit_quant_type="nf4", bnb_4bit_use_double_quant=True, bnb_4bit_compute_dtype=torch.float16)
    model = AutoModelForCausalLM.from_pretrained(base_path, quantization_config=quantization, torch_dtype=torch.float16, device_map="cuda")
    
    model.config.use_cache = False
    
    # CRITICAL: Enable gradient checkpointing to save VRAM
    model.gradient_checkpointing_enable()
    
    model = prepare_model_for_kbit_training(model)
    if config.source_adapter_dir is not None:
        source_adapter = Path(config.source_adapter_dir)
        if not source_adapter.is_dir():
            raise FileNotFoundError(f"Source adapter {source_adapter} not found")
        model = PeftModel.from_pretrained(model, source_adapter, is_trainable=True)
    else:
        model = get_peft_model(
            model,
            LoraConfig(
                r=config.lora_rank,
                lora_alpha=config.lora_alpha,
                lora_dropout=config.lora_dropout,
                target_modules=list(config.target_modules),
                bias="none",
                task_type="CAUSAL_LM",
            ),
        )

    def encode(path: Path):
        rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
        return [encode_llama_response(tokenizer, tuple(LlamaMessage(message["role"], message["content"]) for message in row["messages"]), row["response"], config.max_length) for row in rows]

    train_rows, validation_rows = encode(train_path), encode(validation_path)
    collator = LlamaResponseCollator(tokenizer.pad_token_id)
    optimizer = torch.optim.AdamW((parameter for parameter in model.parameters() if parameter.requires_grad), lr=config.learning_rate)
    random.Random(config.seed).shuffle(train_rows)
    
    limit = max_steps if max_steps is not None else len(train_rows) * config.epochs
    if limit <= 0:
        raise ValueError("max_steps must be positive")
        
    def validation_loss() -> float:
        model.eval()
        losses = []
        with torch.inference_mode():
            for row in validation_rows:
                batch = {key: value.to("cuda") for key, value in collator([row]).items()}
                losses.append(float(model(**batch).loss))
        model.train()
        return sum(losses) / len(losses) if losses else float('inf')

    adapter_dir = Path(config.adapter_dir)
    model.train()
    optimizer.zero_grad(set_to_none=True)
    best_loss = float("inf")
    optimizer_updates = 0
    monitor = (
        PhysicalVramMonitor(config.physical_vram_cap_mib, config.physical_vram_poll_seconds)
        if config.physical_vram_cap_mib is not None
        else None
    )
    if monitor is not None:
        monitor.start()

    try:
        for step in range(limit):
            if monitor is not None and monitor.exceeded:
                raise RuntimeError("physical VRAM cap exceeded")
            batch = collator([train_rows[step % len(train_rows)]])
            batch = {key: value.to("cuda") for key, value in batch.items()}
            loss = model(**batch).loss / config.gradient_accumulation_steps
            loss.backward()
            if (step + 1) % config.gradient_accumulation_steps == 0 or step + 1 == limit:
                optimizer.step()
                optimizer.zero_grad(set_to_none=True)
                optimizer_updates += 1
                if optimizer_updates % config.evaluation_steps == 0 or step + 1 == limit:
                    candidate_loss = validation_loss()
                    if monitor is not None and monitor.exceeded:
                        raise RuntimeError("physical VRAM cap exceeded")
                    if candidate_loss < best_loss:
                        best_loss = candidate_loss
                        model.save_pretrained(adapter_dir)
    finally:
        if monitor is not None:
            monitor.stop()

    physical_peak_mib = monitor.peak_mib if monitor is not None else None
    physical_cap_passed = None
    if monitor is not None:
        if physical_peak_mib is None:
            raise RuntimeError("physical VRAM measurement unavailable")
        if monitor.exceeded or physical_peak_mib > config.physical_vram_cap_mib:
            raise RuntimeError("physical VRAM cap exceeded")
        physical_cap_passed = True

    peak_vram_mb = torch.cuda.max_memory_allocated() / (1024 * 1024)
    return write_adapter_manifest(
        adapter_dir,
        backend=config.backend,
        profile_name=config.profile_name,
        base_model_id=config.base_model_id,
        base_model_path=str(base_path),
        source_adapter_dir=config.source_adapter_dir,
        dataset_sha256=dataset_sha256,
        best_validation_loss=best_loss,
        peak_vram_mb=peak_vram_mb,
        physical_peak_mib=physical_peak_mib,
        physical_cap_mib=config.physical_vram_cap_mib,
        physical_cap_passed=physical_cap_passed,
        seed=config.seed,
        max_length=config.max_length,
        optimizer_updates=optimizer_updates,
    )
