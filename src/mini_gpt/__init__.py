"""Educational bilingual mini-GPT package."""

from .config import ModelConfig, TrainingConfig
from .model import MiniGPT

__all__ = ["MiniGPT", "ModelConfig", "TrainingConfig"]

