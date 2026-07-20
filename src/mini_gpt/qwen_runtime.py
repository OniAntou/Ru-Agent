from __future__ import annotations

import json
from collections.abc import Callable
from dataclasses import dataclass
from importlib.util import find_spec
from pathlib import Path


@dataclass(frozen=True)
class QwenBaseProfile:
    model_id: str
    local_path: str
    label: str


QWEN_V3_BASE = QwenBaseProfile(
    model_id="Qwen/Qwen2.5-0.5B-Instruct",
    local_path="models/qwen2.5-0.5b-instruct",
    label="Ru V3 Qwen 0.5B",
)
QWEN_V4_BASE = QwenBaseProfile(
    model_id="Qwen/Qwen2.5-1.5B-Instruct",
    local_path="models/qwen2.5-1.5b-instruct",
    label="Ru V4 Qwen 1.5B",
)
QWEN_MODEL_ID = QWEN_V3_BASE.model_id
QWEN_BASE_PROFILES = {profile.model_id: profile for profile in (QWEN_V3_BASE, QWEN_V4_BASE)}
REQUIRED_QWEN_PACKAGES = ("transformers", "accelerate", "peft", "bitsandbytes")


def qwen_base_profile(model_id: str) -> QwenBaseProfile:
    try:
        return QWEN_BASE_PROFILES[model_id]
    except KeyError as error:
        raise ValueError(f"model_id must be a registered Ru Qwen base, got {model_id!r}") from error


def qwen_dependency_status(
    resolver: Callable[[str], object | None] = find_spec,
) -> dict[str, bool]:
    return {name: resolver(name) is not None for name in REQUIRED_QWEN_PACKAGES}


def require_qwen_dependencies(
    resolver: Callable[[str], object | None] = find_spec,
) -> None:
    missing = [name for name, present in qwen_dependency_status(resolver).items() if not present]
    if missing:
        raise RuntimeError("install the Qwen runtime extra: " + ", ".join(missing))


def verify_local_qwen_base(path: str | Path) -> dict[str, object]:
    model_path = Path(path)
    config_path = model_path / "config.json"
    if not config_path.is_file():
        raise FileNotFoundError(f"Qwen config not found: {config_path}")
    config = json.loads(config_path.read_text(encoding="utf-8"))
    if not isinstance(config, dict) or config.get("model_type") != "qwen2":
        raise ValueError("local base model_type must be qwen2")
    if not any(model_path.glob("*.safetensors")):
        raise FileNotFoundError("local Qwen safetensors weights not found")
    return config
