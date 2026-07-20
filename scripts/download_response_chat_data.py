from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.chat_sources import download_response_chat_seed  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Stream bounded V2 Vietnamese-English chat data to D:")
    parser.add_argument("--output", default=str(PROJECT_ROOT / "data" / "conversations" / "response_v2_seed.jsonl"))
    parser.add_argument(
        "--manifest",
        default=str(PROJECT_ROOT / "data" / "conversations" / "response_v2_seed_manifest.json"),
    )
    parser.add_argument("--cache-dir", default=str(PROJECT_ROOT / ".hf"))
    parser.add_argument("--max-5cd", type=int, default=18_000)
    parser.add_argument("--max-vietnamese", type=int, default=1_797)
    parser.add_argument("--max-oasst", type=int, default=2_000)
    parser.add_argument("--max-ultrachat", type=int, default=6_000)
    return parser


def main(argv: list[str] | None = None) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = build_parser().parse_args(argv)
    manifest = download_response_chat_seed(
        output_path=args.output,
        manifest_path=args.manifest,
        cache_dir=args.cache_dir,
        max_5cd_examples=args.max_5cd,
        max_vietnamese_examples=args.max_vietnamese,
        max_oasst_examples=args.max_oasst,
        max_ultrachat_examples=args.max_ultrachat,
    )
    print(json.dumps(manifest, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
