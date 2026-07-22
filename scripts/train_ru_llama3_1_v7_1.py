from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.llama3_1_training import (  # noqa: E402
    LlamaTrainingConfig,
    train_ru_adapter_llama,
    validate_v7_1_adapter_dir,
)


EXPECTED_ADAPTER_DIR = PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v7.1"


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Train Ru Llama 3.1 v7.1 conversation-repair adapter")
    parser.add_argument(
        "--config",
        default=str(PROJECT_ROOT / "configs" / "ru_llama3_1_8b_v7_1.json"),
    )
    parser.add_argument("--max-steps", type=int)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    config = LlamaTrainingConfig.from_json(args.config)
    adapter_dir = validate_v7_1_adapter_dir(config.adapter_dir)
    if adapter_dir.resolve() != EXPECTED_ADAPTER_DIR.resolve():
        raise ValueError(f"v7.1 training must write to {EXPECTED_ADAPTER_DIR}")
    manifest = train_ru_adapter_llama(config, args.max_steps)
    print(json.dumps(manifest, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
