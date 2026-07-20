from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.data_sources import download_wikipedia_sample  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Stream a bounded Vietnamese-English Wikipedia sample to D:")
    parser.add_argument("--output-dir", default=str(PROJECT_ROOT / "data" / "raw"))
    parser.add_argument("--cache-dir", default=str(PROJECT_ROOT / ".hf"))
    parser.add_argument("--languages", nargs="+", default=["en", "vi"], choices=["en", "vi"])
    parser.add_argument("--snapshot", default="20231101")
    parser.add_argument("--max-documents", type=int, default=10_000)
    parser.add_argument("--min-characters", type=int, default=200)
    parser.add_argument("--max-characters", type=int, default=100_000)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    manifest = download_wikipedia_sample(
        output_dir=args.output_dir,
        languages=args.languages,
        snapshot=args.snapshot,
        max_documents_per_language=args.max_documents,
        min_characters=args.min_characters,
        max_characters=args.max_characters,
        cache_dir=args.cache_dir,
    )
    print(json.dumps(manifest, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

