from __future__ import annotations

import math
import random
from contextlib import nullcontext
from pathlib import Path
from typing import Iterable

import numpy as np
import torch
from torch.utils.data import DataLoader, Dataset

from .checkpointing import load_checkpoint, save_checkpoint
from .config import ModelConfig, TrainingConfig
from .model import MiniGPT


def seed_everything(seed: int) -> None:
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(seed)


def resolve_device(value: str) -> torch.device:
    if value == "auto":
        return torch.device("cuda" if torch.cuda.is_available() else "cpu")
    if value == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA was requested but torch.cuda.is_available() is false")
    return torch.device(value)


def _learning_rate_multiplier(step: int, warmup_steps: int, total_steps: int) -> float:
    if step < warmup_steps:
        return max(1e-6, (step + 1) / max(1, warmup_steps))
    progress = min(1.0, (step - warmup_steps) / max(1, total_steps - warmup_steps))
    return 0.5 * (1.0 + math.cos(math.pi * progress))


def evaluate_loss(
    model: MiniGPT,
    loader: Iterable[tuple[torch.Tensor, torch.Tensor]],
    device: torch.device,
    max_batches: int,
    amp_enabled: bool,
) -> float:
    model.eval()
    total_loss = 0.0
    batches = 0
    with torch.no_grad():
        for input_ids, targets in loader:
            input_ids = input_ids.to(device)
            targets = targets.to(device)
            autocast = torch.autocast("cuda", dtype=torch.float16, enabled=amp_enabled and device.type == "cuda")
            with autocast:
                _, loss = model(input_ids, targets)
            if loss is not None:
                total_loss += float(loss.item())
            batches += 1
            if batches >= max_batches:
                break
    model.train()
    return total_loss / max(1, batches)


def run_training(
    model_config: ModelConfig,
    training_config: TrainingConfig,
    train_dataset: Dataset[tuple[torch.Tensor, torch.Tensor]],
    validation_dataset: Dataset[tuple[torch.Tensor, torch.Tensor]],
    checkpoint_dir: str | Path,
    resume_path: str | Path | None = None,
) -> dict[str, float | int | str]:
    seed_everything(training_config.seed)
    device = resolve_device(training_config.device)
    model = MiniGPT(model_config).to(device)
    optimizer = torch.optim.AdamW(
        model.parameters(),
        lr=training_config.learning_rate,
        weight_decay=training_config.weight_decay,
    )
    scheduler = torch.optim.lr_scheduler.LambdaLR(
        optimizer,
        lambda step: _learning_rate_multiplier(step, training_config.warmup_steps, training_config.total_steps),
    )
    train_loader = DataLoader(train_dataset, batch_size=training_config.batch_size, shuffle=True)
    validation_loader = DataLoader(validation_dataset, batch_size=training_config.batch_size, shuffle=False)
    iterator = iter(train_loader)
    scaler = torch.amp.GradScaler("cuda", enabled=training_config.amp and device.type == "cuda")
    start_step = 0
    best_validation_loss = float("inf")
    if resume_path is not None:
        payload = load_checkpoint(resume_path, model, optimizer, scheduler, device)
        start_step = int(payload["step"])
        best_validation_loss = float(payload["best_validation_loss"])

    model.train()
    optimizer.zero_grad(set_to_none=True)
    latest_validation_loss = float("inf")
    for step in range(start_step, training_config.total_steps):
        raw_loss = 0.0
        for _ in range(training_config.gradient_accumulation_steps):
            try:
                input_ids, targets = next(iterator)
            except StopIteration:
                iterator = iter(train_loader)
                input_ids, targets = next(iterator)
            input_ids = input_ids.to(device)
            targets = targets.to(device)
            autocast = torch.autocast("cuda", dtype=torch.float16, enabled=training_config.amp and device.type == "cuda")
            with autocast:
                _, loss = model(input_ids, targets)
                if loss is None:
                    raise RuntimeError("training requires a loss from the model")
                scaled_loss = loss / training_config.gradient_accumulation_steps
            scaler.scale(scaled_loss).backward()
            raw_loss += float(loss.item())
        scaler.unscale_(optimizer)
        torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
        scaler.step(optimizer)
        scaler.update()
        scheduler.step()
        completed_step = step + 1

        if completed_step % training_config.log_interval == 0 or completed_step == 1:
            memory = torch.cuda.max_memory_allocated(device) / 1024**2 if device.type == "cuda" else 0.0
            print(
                f"step={completed_step} train_loss={raw_loss / training_config.gradient_accumulation_steps:.4f} "
                f"lr={scheduler.get_last_lr()[0]:.6g} device={device} peak_mb={memory:.1f}",
                flush=True,
            )
        if completed_step % training_config.validation_interval == 0 or completed_step == training_config.total_steps:
            latest_validation_loss = evaluate_loss(
                model,
                validation_loader,
                device,
                training_config.max_validation_batches,
                training_config.amp,
            )
            best_validation_loss = min(best_validation_loss, latest_validation_loss)
            print(f"validation step={completed_step} loss={latest_validation_loss:.4f}", flush=True)
        if completed_step % training_config.checkpoint_interval == 0 or completed_step == training_config.total_steps:
            save_checkpoint(
                Path(checkpoint_dir) / f"step-{completed_step:07d}.pt",
                model,
                optimizer,
                scheduler,
                completed_step,
                best_validation_loss,
                model_config,
                training_config,
            )
            save_checkpoint(
                Path(checkpoint_dir) / "latest.pt",
                model,
                optimizer,
                scheduler,
                completed_step,
                best_validation_loss,
                model_config,
                training_config,
            )
    return {
        "device": str(device),
        "parameter_count": model.parameter_count(),
        "step": training_config.total_steps,
        "validation_loss": latest_validation_loss,
        "best_validation_loss": best_validation_loss,
    }

