from __future__ import annotations

import json
import random
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

from .qwen_runtime import qwen_base_profile


@dataclass
class QwenTrainingConfig:
    base_model_id: str
    base_model_path: str | None = None
    dataset_dir: str = "data/conversations/ru_v3"
    adapter_dir: str = "checkpoints/ru-qwen-v3/adapter"
    max_length: int = 512
    seed: int = 42
    epochs: int = 1
    learning_rate: float = 5e-5
    batch_size: int = 1
    gradient_accumulation_steps: int = 8
    evaluation_steps: int = 4
    save_steps: int = 4
    logging_steps: int = 1
    lora_rank: int = 16
    lora_alpha: int = 32
    lora_dropout: float = 0.05
    target_modules: tuple[str, ...] = ("q_proj", "k_proj", "v_proj", "o_proj")
    backend: str = "qwen-qlora-v3"
    profile_name: str = "ru_qwen_v3"

    def __post_init__(self) -> None:
        profile = qwen_base_profile(self.base_model_id)
        if self.base_model_path is None:
            self.base_model_path = profile.local_path
        elif self.base_model_path.replace("\\", "/") != profile.local_path:
            raise ValueError("base_model_path must match the registered Ru Qwen base path")
        if not self.backend or not self.profile_name:
            raise ValueError("Qwen backend and profile_name cannot be empty")

    @classmethod
    def from_json(cls, path: str | Path) -> "QwenTrainingConfig":
        payload = json.loads(Path(path).read_text(encoding="utf-8"))
        if not isinstance(payload, dict):
            raise ValueError("Qwen training config must be an object")
        if "target_modules" in payload:
            payload["target_modules"] = tuple(payload["target_modules"])
        return cls(**payload)


def write_adapter_manifest(adapter_dir: str | Path, *, backend: str, profile_name: str, base_model_id: str, base_model_path: str, dataset_sha256: str, best_validation_loss: float, peak_vram_mb: float, seed: int, max_length: int, optimizer_updates: int = 0) -> dict[str, object]:
    manifest = {"backend": backend, "profile_name": profile_name, "base_model_id": base_model_id, "base_model_path": base_model_path, "dataset_sha256": dataset_sha256, "best_validation_loss": best_validation_loss, "peak_vram_mb": peak_vram_mb, "seed": seed, "max_length": max_length, "optimizer_updates": optimizer_updates, "generated_at_utc": datetime.now(timezone.utc).isoformat()}
    destination = Path(adapter_dir)
    destination.mkdir(parents=True, exist_ok=True)
    (destination / "ru_manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def train_ru_adapter(config: QwenTrainingConfig, max_steps: int | None = None) -> dict[str, object]:
    from hashlib import sha256

    import torch
    from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training
    from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig

    from .qwen_data import QwenMessage, QwenResponseCollator, encode_qwen_response
    from .qwen_runtime import require_qwen_dependencies, verify_local_qwen_base

    require_qwen_dependencies()
    if not torch.cuda.is_available():
        raise RuntimeError("Qwen QLoRA training requires CUDA")
    base_path = Path(config.base_model_path or "")
    verify_local_qwen_base(base_path)
    dataset_dir = Path(config.dataset_dir)
    train_path, validation_path, manifest_path = dataset_dir / "train.jsonl", dataset_dir / "val.jsonl", dataset_dir / "manifest.json"
    if not train_path.is_file() or not validation_path.is_file() or not manifest_path.is_file():
        raise FileNotFoundError("Ru V3 train, validation, or manifest data is missing")
    dataset_sha256 = sha256(manifest_path.read_bytes()).hexdigest()
    tokenizer = AutoTokenizer.from_pretrained(base_path)
    tokenizer.pad_token = tokenizer.pad_token or tokenizer.eos_token
    quantization = BitsAndBytesConfig(load_in_4bit=True, bnb_4bit_quant_type="nf4", bnb_4bit_use_double_quant=True, bnb_4bit_compute_dtype=torch.float16)
    model = AutoModelForCausalLM.from_pretrained(base_path, quantization_config=quantization, torch_dtype=torch.float16, device_map="cuda")
    model.config.use_cache = False
    model = prepare_model_for_kbit_training(model)
    model = get_peft_model(model, LoraConfig(r=config.lora_rank, lora_alpha=config.lora_alpha, lora_dropout=config.lora_dropout, target_modules=list(config.target_modules), bias="none", task_type="CAUSAL_LM"))

    def encode(path: Path):
        rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
        return [encode_qwen_response(tokenizer, tuple(QwenMessage(message["role"], message["content"]) for message in row["messages"]), row["response"], config.max_length) for row in rows]

    train_rows, validation_rows = encode(train_path), encode(validation_path)
    collator = QwenResponseCollator(tokenizer.pad_token_id)
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
        return sum(losses) / len(losses)

    adapter_dir = Path(config.adapter_dir)
    model.train()
    optimizer.zero_grad(set_to_none=True)
    best_loss = float("inf")
    optimizer_updates = 0
    for step in range(limit):
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
                if candidate_loss < best_loss:
                    best_loss = candidate_loss
                    model.save_pretrained(adapter_dir)
    peak_vram_mb = torch.cuda.max_memory_allocated() / (1024 * 1024)
    if peak_vram_mb > 5500:
        raise RuntimeError(f"peak VRAM {peak_vram_mb:.1f} MiB exceeds 5500 MiB safety limit")
    return write_adapter_manifest(adapter_dir, backend=config.backend, profile_name=config.profile_name, base_model_id=config.base_model_id, base_model_path=str(base_path), dataset_sha256=dataset_sha256, best_validation_loss=best_loss, peak_vram_mb=peak_vram_mb, seed=config.seed, max_length=config.max_length, optimizer_updates=optimizer_updates)
