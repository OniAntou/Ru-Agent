from __future__ import annotations

from collections.abc import Callable
from importlib.util import find_spec

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
