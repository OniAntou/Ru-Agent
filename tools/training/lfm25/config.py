from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Literal


@dataclass(frozen=True)
class Lfm25TrainingConfig:
    base_model_id: str = "LiquidAI/LFM2.5-2.6B"
    base_model_path: str = "models/lfm2.5-2.6b-hf"
    dataset_dir: str = "data/conversations/ru_v1"
    adapter_dir: str = "checkpoints/ru-lfm2.5-2.6b-v1"
    max_length: int = 384
    seed: int = 42
    epochs: int = 3
    max_steps: int = 1600
    learning_rate: float = 8e-5
    batch_size: int = 1
    gradient_accumulation_steps: int = 16
    evaluation_steps: int = 40
    validation_max_examples: int = 36
    save_steps: int = 40
    logging_steps: int = 1
    lora_rank: int = 8
    lora_alpha: int = 16
    lora_dropout: float = 0.05
    target_modules: tuple[str, ...] = (
        "self_attn.q_proj",
        "self_attn.k_proj",
        "self_attn.v_proj",
        "self_attn.out_proj",
        "feed_forward.w1",
        "feed_forward.w3",
    )
    backend: str = "lfm2.5-qlora"
    profile_name: str = "ru_lfm2_5_v1"
    physical_vram_cap_mib: float | None = 6144
    physical_vram_poll_seconds: float = 0.5
    wall_clock_seconds: float | None = 9600
    training_status: Literal["not_started", "approved"] = "not_started"

    @classmethod
    def from_json(cls, path: str | Path) -> "Lfm25TrainingConfig":
        payload = json.loads(Path(path).read_text(encoding="utf-8"))
        if not isinstance(payload, dict):
            raise ValueError("Training config must be an object")
        if "target_modules" in payload:
            payload["target_modules"] = tuple(payload["target_modules"])
        return cls(**payload)