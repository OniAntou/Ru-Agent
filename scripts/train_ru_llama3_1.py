from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.llama3_1_training import LlamaTrainingConfig, train_ru_adapter_llama  # noqa: E402

parser = argparse.ArgumentParser(description="Train Ru's Llama 3.1 8B candidate")
parser.add_argument("--config", required=True, help="Reviewed training config JSON")
parser.add_argument("--max-steps", type=int)
args = parser.parse_args()
print(json.dumps(train_ru_adapter_llama(LlamaTrainingConfig.from_json(args.config), args.max_steps), indent=2))
