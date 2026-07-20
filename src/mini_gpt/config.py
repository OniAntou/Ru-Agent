from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any


@dataclass
class ModelConfig:
    vocab_size: int = 16_384
    context_length: int = 512
    n_layers: int = 8
    d_model: int = 512
    n_heads: int = 8
    d_ff: int = 2_048
    dropout: float = 0.0
    tie_embeddings: bool = True

    def __post_init__(self) -> None:
        self.validate()

    def validate(self) -> None:
        positive = {
            "vocab_size": self.vocab_size,
            "context_length": self.context_length,
            "n_layers": self.n_layers,
            "d_model": self.d_model,
            "n_heads": self.n_heads,
            "d_ff": self.d_ff,
        }
        for name, value in positive.items():
            if value <= 0:
                raise ValueError(f"{name} must be positive")
        if self.d_model % self.n_heads:
            raise ValueError("d_model must be divisible by n_heads")
        if not 0 <= self.dropout < 1:
            raise ValueError("dropout must be in [0, 1)")

    @classmethod
    def tiny(cls, vocab_size: int) -> "ModelConfig":
        return cls(
            vocab_size=vocab_size,
            context_length=128,
            n_layers=2,
            d_model=128,
            n_heads=4,
            d_ff=512,
            dropout=0.0,
        )

    @classmethod
    def from_dict(cls, values: dict[str, Any], *, vocab_size: int | None = None) -> "ModelConfig":
        payload = dict(values)
        if vocab_size is not None:
            payload["vocab_size"] = vocab_size
        return cls(**payload)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass
class TrainingConfig:
    seed: int = 42
    batch_size: int = 1
    gradient_accumulation_steps: int = 8
    learning_rate: float = 3e-4
    weight_decay: float = 0.1
    warmup_steps: int = 100
    total_steps: int = 1_000
    validation_interval: int = 100
    checkpoint_interval: int = 100
    log_interval: int = 10
    max_validation_batches: int = 20
    device: str = "auto"
    amp: bool = True

    def __post_init__(self) -> None:
        self.validate()

    def validate(self) -> None:
        for name in (
            "batch_size",
            "gradient_accumulation_steps",
            "warmup_steps",
            "total_steps",
            "validation_interval",
            "checkpoint_interval",
            "log_interval",
            "max_validation_batches",
        ):
            if getattr(self, name) <= 0:
                raise ValueError(f"{name} must be positive")
        if self.learning_rate <= 0 or self.weight_decay < 0:
            raise ValueError("learning_rate must be positive and weight_decay cannot be negative")
        if self.device not in {"auto", "cpu", "cuda"}:
            raise ValueError("device must be auto, cpu, or cuda")

    @classmethod
    def from_dict(cls, values: dict[str, Any]) -> "TrainingConfig":
        return cls(**values)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

