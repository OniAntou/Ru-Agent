from __future__ import annotations

import os
import random
from pathlib import Path
from typing import Any

import numpy as np
import torch

from .config import ModelConfig, TrainingConfig


def capture_rng_state() -> dict[str, Any]:
    return {
        "python": random.getstate(),
        "numpy": np.random.get_state(),
        "torch": torch.get_rng_state(),
        "cuda": torch.cuda.get_rng_state_all() if torch.cuda.is_available() else None,
    }


def restore_rng_state(state: dict[str, Any]) -> None:
    random.setstate(state["python"])
    np.random.set_state(state["numpy"])
    torch.set_rng_state(state["torch"])
    if state.get("cuda") is not None and torch.cuda.is_available():
        torch.cuda.set_rng_state_all(state["cuda"])


def save_checkpoint(
    path: str | Path,
    model: torch.nn.Module,
    optimizer: torch.optim.Optimizer,
    scheduler: torch.optim.lr_scheduler.LRScheduler,
    step: int,
    best_validation_loss: float,
    model_config: ModelConfig,
    training_config: TrainingConfig,
) -> None:
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    payload = {
        "model": model.state_dict(),
        "optimizer": optimizer.state_dict(),
        "scheduler": scheduler.state_dict(),
        "step": step,
        "best_validation_loss": best_validation_loss,
        "model_config": model_config.to_dict(),
        "training_config": training_config.to_dict(),
        "rng_state": capture_rng_state(),
    }
    torch.save(payload, temporary)
    os.replace(temporary, destination)


def load_checkpoint(
    path: str | Path,
    model: torch.nn.Module,
    optimizer: torch.optim.Optimizer | None = None,
    scheduler: torch.optim.lr_scheduler.LRScheduler | None = None,
    device: torch.device | str = "cpu",
    restore_rng: bool = True,
) -> dict[str, Any]:
    checkpoint_path = Path(path)
    if not checkpoint_path.is_file():
        raise FileNotFoundError(f"checkpoint not found: {checkpoint_path}")
    try:
        payload = torch.load(checkpoint_path, map_location=device, weights_only=False)
    except TypeError:
        payload = torch.load(checkpoint_path, map_location=device)
    if payload["model_config"] != model.config.to_dict():
        raise ValueError("checkpoint model configuration does not match the current model")
    model.load_state_dict(payload["model"])
    if optimizer is not None:
        optimizer.load_state_dict(payload["optimizer"])
    if scheduler is not None:
        scheduler.load_state_dict(payload["scheduler"])
    if restore_rng:
        restore_rng_state(payload["rng_state"])
    return payload
