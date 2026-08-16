from __future__ import annotations

import argparse
from pathlib import Path

from tools.training.lfm25.config import Lfm25TrainingConfig
from tools.training.lfm25.train import train_lfm25_adapter


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run the supported bounded LFM2.5 QLoRA training")
    parser.add_argument(
        "--config",
        default=str(Path(__file__).resolve().parents[3] / "configs" / "ru_lfm2_5_v1.json"),
    )
    parser.add_argument("--max-steps", type=int, default=None)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    config = Lfm25TrainingConfig.from_json(args.config)
    manifest = train_lfm25_adapter(config, max_steps=args.max_steps)
    print(f"[train] manifest: {manifest['adapter_dir']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())