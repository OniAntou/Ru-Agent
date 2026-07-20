from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.qwen_training import QwenTrainingConfig, train_ru_adapter  # noqa: E402

parser = argparse.ArgumentParser(description="Train the local Ru Qwen QLoRA adapter")
parser.add_argument("--config", default=str(PROJECT_ROOT / "configs" / "ru_qwen_v3.json"))
parser.add_argument("--max-steps", type=int)
args = parser.parse_args()
if args.max_steps is not None and args.max_steps <= 0:
    parser.error("--max-steps must be positive")
print(json.dumps(train_ru_adapter(QwenTrainingConfig.from_json(args.config), args.max_steps), indent=2))
