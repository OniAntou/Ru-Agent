from __future__ import annotations

import argparse
import json
import sys
import warnings
from pathlib import Path
from tempfile import TemporaryDirectory

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.ru_v6_corpus import build_ru_v6_corpus
from mini_gpt.ru_v6_dialogues import v6_dialogue_records
from mini_gpt.ru_v6_1_corpus import build_ru_v6_1_corpus
from mini_gpt.ru_v6_2_corpus import build_ru_v6_2_corpus
from mini_gpt.ru_v6_2_fresh_corpus import build_ru_v6_2_fresh_corpus
from mini_gpt.ru_v6_3_corpus import build_ru_v6_3_corpus

VERSION_DEFAULTS = {
    "v6": {"dir": "ru_v6", "seed": 42, "deprecated": True},
    "v6_1": {"dir": "ru_v6_1", "seed": 42},
    "v6_2": {"dir": "ru_v6_2", "seed": 42},
    "v6_2_fresh": {"dir": "ru_v6_2_fresh", "seed": 62},
    "v6_3": {"dir": "ru_v6_3", "seed": 63},
}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Build any Ru corpus version from a single CLI")
    parser.add_argument("--version", required=True, choices=sorted(VERSION_DEFAULTS))
    parser.add_argument("--output-dir", type=Path, default=None)
    parser.add_argument("--seed", type=int, default=None)
    parser.add_argument("--audit", action="store_true", help="v6/v6.1/v6.2: build to a temp dir and dump held-out rows")
    parser.add_argument("--audit-only", action="store_true", help="v6.2_fresh/v6.3: validate without writing")
    parser.add_argument(
        "--approved-file",
        type=Path,
        default=PROJECT_ROOT / "data" / "conversations" / "approved.jsonl",
    )
    parser.add_argument(
        "--memory-file",
        type=Path,
        default=PROJECT_ROOT / "data" / "conversations" / "memory_texts.json",
    )
    return parser


def _rows(path: Path) -> list[dict[str, object]]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def _dump(report: dict[str, object], held_out: list[dict[str, object]] | None = None) -> None:
    payload: dict[str, object] = {"report": report}
    if held_out is not None:
        payload["held_out"] = held_out
    print(json.dumps(payload, ensure_ascii=False, indent=2))


def _run_v6(args: argparse.Namespace) -> int:
    if args.audit_only:
        raise SystemExit("--audit-only is not supported for v6; use --audit")
    warnings.warn("V6 corpus is deprecated; use v6.1+ corpora instead", DeprecationWarning, stacklevel=2)
    records = v6_dialogue_records()
    if args.audit:
        with TemporaryDirectory(prefix="ru-v6-audit-") as temporary_dir:
            report = build_ru_v6_corpus(records, temporary_dir, seed=args.seed)
            held_out = _rows(Path(temporary_dir) / "held_out.jsonl")
        _dump(report, held_out)
        return 0
    _dump(build_ru_v6_corpus(records, args.output_dir, seed=args.seed))
    return 0


def _run_v6_1_2(args: argparse.Namespace) -> int:
    if args.audit_only:
        raise SystemExit(f"--audit-only is not supported for {args.version}; use --audit")
    builder = build_ru_v6_1_corpus if args.version == "v6_1" else build_ru_v6_2_corpus
    if args.audit:
        with TemporaryDirectory(prefix=f"ru-{args.version}-audit-") as temporary_dir:
            report = builder(temporary_dir, seed=args.seed)
            held_out = _rows(Path(temporary_dir) / "held_out.jsonl")
        _dump(report, held_out)
        return 0
    _dump(builder(args.output_dir, seed=args.seed))
    return 0


def _run_fresh(args: argparse.Namespace) -> int:
    if not args.audit_only:
        for path, label in ((args.approved_file, "approved-file"), (args.memory_file, "memory-file")):
            if not path.is_file():
                raise SystemExit(f"{label} not found: {path}")
    _dump(
        build_ru_v6_2_fresh_corpus(
            args.output_dir,
            approved_file=args.approved_file,
            memory_file=args.memory_file,
            seed=args.seed,
            write=not args.audit_only,
        )
    )
    return 0


def _run_v6_3(args: argparse.Namespace) -> int:
    _dump(build_ru_v6_3_corpus(args.output_dir, seed=args.seed, write=not args.audit_only))
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    defaults = VERSION_DEFAULTS[args.version]
    args.output_dir = args.output_dir or (PROJECT_ROOT / "data" / "conversations" / defaults["dir"])
    args.seed = args.seed if args.seed is not None else defaults["seed"]

    if args.version == "v6":
        return _run_v6(args)
    if args.version in {"v6_1", "v6_2"}:
        return _run_v6_1_2(args)
    if args.version == "v6_2_fresh":
        return _run_fresh(args)
    return _run_v6_3(args)


if __name__ == "__main__":
    raise SystemExit(main())
