from __future__ import annotations

import json
from collections.abc import Callable
from importlib.util import find_spec
from pathlib import Path

QWEN_MODEL_ID = "Qwen/Qwen2.5-0.5B-Instruct"
REQUIRED_QWEN_PACKAGES = ("transformers", "accelerate", "peft", "bitsandbytes")


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
