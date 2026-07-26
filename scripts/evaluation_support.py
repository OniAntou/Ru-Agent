from __future__ import annotations

import json
from pathlib import Path


def bytes_to_mib(value: int) -> float:
    return round(value / (1024 * 1024), 3)


def load_evaluation_config(path: str | Path) -> dict[str, object]:
    config = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(config, dict):
        raise ValueError("evaluation config must be a JSON object")
    base_model_id = config.get("base_model_id")
    if not isinstance(base_model_id, str) or not base_model_id:
        raise ValueError("evaluation config must include a non-empty base_model_id")
    profile_name = config.get("profile_name", base_model_id)
    phase = config.get("phase", "comparison")
    if not isinstance(profile_name, str) or not profile_name:
        raise ValueError("evaluation config profile_name must be a non-empty string")
    if not isinstance(phase, str) or not phase:
        raise ValueError("evaluation config phase must be a non-empty string")
    return {**config, "profile_name": profile_name, "phase": phase}
