from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.ru_authored_dialogues import authored_dialogue_records  # noqa: E402
from mini_gpt.ru_qwen_corpus import build_ru_qwen_corpus  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Build the local authored Ru Qwen V3 conversation corpus")
    parser.add_argument("--output-dir", default=str(PROJECT_ROOT / "data" / "conversations" / "ru_v3"))
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--audit", action="store_true", help="Print a 24-row stratified quality audit without writing files")
    return parser


def stratified_audit(records: list[dict[str, object]]) -> list[dict[str, object]]:
    buckets = (
        [record for record in records if record["language"] == "vi" and len(record["messages"]) == 2],
        [record for record in records if record["language"] == "en" and len(record["messages"]) == 2],
        [record for record in records if record["language"] == "vi" and len(record["messages"]) > 2],
        [record for record in records if record["language"] == "en" and len(record["messages"]) > 2],
    )
    if any(len(bucket) < 6 for bucket in buckets):
        raise ValueError("authored audit requires six records in each language/context bucket")
    audit: list[dict[str, object]] = []
    for index in range(6):
        for bucket in buckets:
            record = bucket[index]
            messages = record["messages"]
            assert isinstance(messages, list)
            audit.append(
                {
                    "language": record["language"],
                    "turns": len(messages),
                    "prompt": messages[-1]["content"],
                    "response": record["response"],
                }
            )
    return audit


def main(argv: list[str] | None = None) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = build_parser().parse_args(argv)
    records = authored_dialogue_records()
    if args.audit:
        print(json.dumps(stratified_audit(records), ensure_ascii=False, indent=2))
        return 0
    report = build_ru_qwen_corpus(records, output_dir=args.output_dir, seed=args.seed)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
