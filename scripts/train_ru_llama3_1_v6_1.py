from __future__ import annotations

import argparse
import json
import sys
from dataclasses import replace
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.llama3_1_training import (  # noqa: E402
    LlamaTrainingConfig,
    train_ru_adapter_llama,
    validate_v6_1_adapter_dir,
    validate_v6_1_source_adapter_dir,
)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Train the Ru Llama 3.1 v6.1 natural-style adapter")
    parser.add_argument(
        "--config",
        default=str(PROJECT_ROOT / "configs" / "ru_llama3_1_8b_v6_1.json"),
    )
    parser.add_argument("--output-adapter-dir", default=None)
    parser.add_argument("--max-steps", type=int, default=None)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    config = LlamaTrainingConfig.from_json(args.config)
    if config.source_adapter_dir is None:
        raise ValueError("v6.1 config must specify source_adapter_dir")
    source_adapter = validate_v6_1_source_adapter_dir(config.source_adapter_dir)
    output_adapter = validate_v6_1_adapter_dir(args.output_adapter_dir or config.adapter_dir).resolve()
    if output_adapter.resolve() == source_adapter.resolve():
        raise ValueError("v6.1 output cannot overwrite its v6 source adapter")
    if output_adapter.exists() and any(output_adapter.iterdir()):
        raise FileExistsError(f"Refusing to overwrite non-empty v6.1 output directory: {output_adapter}")

    config = replace(
        config,
        source_adapter_dir=str(source_adapter.resolve()),
        adapter_dir=str(output_adapter),
    )
    manifest = train_ru_adapter_llama(config, args.max_steps)
    print(json.dumps(manifest, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
