from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.ru_v6_3_corpus import build_ru_v6_3_corpus  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Build the new authored Ru v6.3 targeted corpus.")
    parser.add_argument("--output-dir", type=Path, default=PROJECT_ROOT / "data" / "conversations" / "ru_v6_3")
    parser.add_argument("--seed", type=int, default=63)
    parser.add_argument("--audit-only", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    report = build_ru_v6_3_corpus(args.output_dir, seed=args.seed, write=not args.audit_only)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
