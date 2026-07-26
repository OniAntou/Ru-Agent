from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.approved_v6_1_import import merge_approved_into_v6_1  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Merge selected approved rows into Ru v6.1 train data.")
    parser.add_argument(
        "--dataset-dir",
        type=Path,
        default=PROJECT_ROOT / "data" / "conversations" / "ru_v6_1",
    )
    parser.add_argument(
        "--approved-file",
        type=Path,
        default=PROJECT_ROOT / "data" / "conversations" / "approved.jsonl",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    report = merge_approved_into_v6_1(args.dataset_dir, args.approved_file)
    print(json.dumps(report["approved_import"], ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
