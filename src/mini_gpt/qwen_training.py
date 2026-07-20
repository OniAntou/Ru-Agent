from __future__ import annotations

import json
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

from .qwen_runtime import QWEN_MODEL_ID


@dataclass(frozen=True)
class QwenTrainingConfig:
    base_model_id: str
    base_model_path: str = "models/qwen2.5-0.5b-instruct"
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

    def __post_init__(self) -> None:
        if self.base_model_id != QWEN_MODEL_ID:
            raise ValueError(f"base_model_id must be {QWEN_MODEL_ID}")


def write_adapter_manifest(adapter_dir: str | Path, *, base_model_id: str, base_model_path: str, dataset_sha256: str, best_validation_loss: float, peak_vram_mb: float, seed: int, max_length: int) -> dict[str, object]:
    manifest = {"backend": "qwen-qlora-v3", "base_model_id": base_model_id, "base_model_path": base_model_path, "dataset_sha256": dataset_sha256, "best_validation_loss": best_validation_loss, "peak_vram_mb": peak_vram_mb, "seed": seed, "max_length": max_length, "generated_at_utc": datetime.now(timezone.utc).isoformat()}
    destination = Path(adapter_dir)
    destination.mkdir(parents=True, exist_ok=True)
    (destination / "ru_manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest
