from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.ru_v7_1_corpus import build_ru_v7_1_corpus  # noqa: E402
from mini_gpt.ru_v7_1_dialogues import v7_1_repair_records  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Build Ru's local v7.1 conversation-repair corpus")
    parser.add_argument("--output-dir", default=str(PROJECT_ROOT / "data" / "conversations" / "ru_v7_1"))
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--audit", action="store_true")
    return parser


def _rows(path: Path) -> list[dict[str, object]]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    records = v7_1_repair_records()
    if args.audit:
        with TemporaryDirectory(prefix="ru-v7-1-audit-") as temporary_dir:
            report = build_ru_v7_1_corpus(records, temporary_dir, seed=args.seed)
            held_out = _rows(Path(temporary_dir) / "held_out.jsonl")
        print(json.dumps({"report": report, "held_out": held_out}, ensure_ascii=False, indent=2))
        return 0
    report = build_ru_v7_1_corpus(records, args.output_dir, seed=args.seed)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
