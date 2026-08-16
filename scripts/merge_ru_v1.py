from __future__ import annotations

import argparse
import hashlib
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from ru_runtime.policy.text import normalize_text  # noqa: E402

DATA_ROOT = PROJECT_ROOT / "data" / "conversations"

# (dataset_dir, split file name) in keep-first order for the global dedup.
SOURCE_GROUPS = [
    ("ru_v6_2", "train.jsonl"),
    ("ru_v6_2", "val.jsonl"),
    ("ru_v6_2", "held_out.jsonl"),
    ("ru_v6_2_fresh", "train.jsonl"),
    ("ru_v6_2_fresh", "val.jsonl"),
    ("ru_v6_2_fresh", "held_out.jsonl"),
    ("ru_v6_3", "train.jsonl"),
    ("ru_v6_3", "val.jsonl"),
    ("ru_v6_3", "held_out.jsonl"),
]

SPLIT_NAMES = {"train": "train", "val": "validation", "held_out": "held_out"}


def _rows(path: Path) -> list[dict[str, object]]:
    rows: list[dict[str, object]] = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        if not line.strip():
            continue
        try:
            rows.append(json.loads(line))
        except json.JSONDecodeError as error:
            raise ValueError(f"Invalid JSON in {path} at line {line_number}: {error}") from error
    return rows


def final_user_prompt(row: dict[str, object]) -> str:
    messages = row.get("messages")
    if not isinstance(messages, list) or not messages:
        raise ValueError("row has no messages list")
    for message in reversed(messages):
        if message.get("role") == "user":
            return normalize_text(str(message.get("content", "")))
    raise ValueError("row has no user message")


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def build_ru_v1_corpus(
    output_dir: str | Path,
    *,
    seed: int = 42,
    write: bool = True,
) -> dict[str, object]:
    destination = Path(output_dir)
    seen: set[str] = set()
    kept: dict[str, list[dict[str, object]]] = defaultdict(list)
    dropped: Counter = Counter()
    input_counts: Counter = Counter()
    for dataset_name, file_name in SOURCE_GROUPS:
        source_path = DATA_ROOT / dataset_name / file_name
        if not source_path.is_file():
            raise FileNotFoundError(f"Source dataset missing: {source_path}")
        for row in _rows(source_path):
            input_counts[(dataset_name, file_name)] += 1
            key = final_user_prompt(row)
            if key in seen:
                dropped[(file_name.split(".")[0], "duplicate_prompt")] += 1
                continue
            seen.add(key)
            kept[file_name].append(row)

    split_counts = {name: 0 for name in SPLIT_NAMES.values()}
    language_counts: Counter = Counter()
    mode_counts: Counter = Counter()
    source_counts: Counter = Counter()
    turn_band_counts: Counter = Counter()
    intensity_counts: Counter = Counter()
    profanity_counts: Counter = Counter()
    for file_name, rows in kept.items():
        split_name = SPLIT_NAMES[file_name.split(".")[0]]
        for row in rows:
            split_counts[split_name] += 1
            language_counts[str(row.get("language", "unknown"))] += 1
            mode_counts[str(row.get("mode", "unknown"))] += 1
            source_counts[str(row.get("source", "unknown"))] += 1
            turn_band_counts[str(row.get("turn_band", "unknown"))] += 1
            intensity_counts[str(row.get("intensity", "unknown"))] += 1
            if "profanity" in row:
                profanity_counts[str(row.get("profanity", "none"))] += 1

    order = {
        "train": list(kept["train.jsonl"]),
        "validation": list(kept["val.jsonl"]),
        "held_out": list(kept["held_out.jsonl"]),
    }

    files: dict[str, str] = {}
    if write:
        destination.mkdir(parents=True, exist_ok=True)
        for split, output_name in (("train", "train.jsonl"), ("validation", "val.jsonl"), ("held_out", "held_out.jsonl")):
            target = destination / output_name
            temp = target.with_suffix(".jsonl.tmp")
            with temp.open("w", encoding="utf-8", newline="\n") as out:
                for row in order[split]:
                    out.write(json.dumps(row, ensure_ascii=False) + "\n")
            temp.replace(target)
        manifest = {
            "dataset_id": "ru_v1",
            "dataset_version": "v1",
            "lineage": "merged from ru_v6_2 + ru_v6_2_fresh + ru_v6_3, global dedup by final user prompt keeping first occurrence",
            "seed": seed,
            "total_examples": sum(split_counts.values()),
            "split_counts": dict(split_counts),
            "language_counts": dict(language_counts),
            "mode_counts": dict(mode_counts),
            "turn_band_counts": dict(turn_band_counts),
            "intensity_counts": dict(intensity_counts),
            "profanity_counts": dict(profanity_counts) if profanity_counts else None,
            "source_counts": dict(source_counts),
            "dedup": {
                "input_rows": sum(input_counts.values()),
                "kept_rows": sum(split_counts.values()),
                "dropped_duplicate_prompts": sum(dropped.values()),
                "dropped_by_split": dict(dropped),
            },
            "input_counts": {f"{name}/{file_name}": count for (name, file_name), count in sorted(input_counts.items())},
            "quality_gates": {
                "dedup_key": "normalized final user prompt",
                "dedup_policy": "keep first occurrence, source order v6_2, v6_2_fresh, v6_3",
            },
        }
        for split, output_name in (("train", "train.jsonl"), ("validation", "val.jsonl"), ("held_out", "held_out.jsonl")):
            manifest[f"{output_name.split('.')[0]}_sha256"] = _sha256_file(destination / output_name)
        manifest_path = destination / "manifest.json"
        temp = manifest_path.with_suffix(".json.tmp")
        temp.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        temp.replace(manifest_path)
        files = {name: _sha256_file(destination / name) for name in ("train.jsonl", "val.jsonl", "manifest.json")}
    return {
        "output_dir": str(destination),
        "seed": seed,
        "split_counts": dict(split_counts),
        "total_examples": sum(split_counts.values()),
        "dropped_duplicate_prompts": sum(dropped.values()),
        "files_sha256": files,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Merge ru_v6_2 + ru_v6_2_fresh + ru_v6_3 into ru_v1")
    parser.add_argument("--output-dir", type=Path, default=DATA_ROOT / "ru_v1")
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--audit-only", action="store_true", help="validate sources without writing")
    args = parser.parse_args(argv)
    print(json.dumps(build_ru_v1_corpus(args.output_dir, seed=args.seed, write=not args.audit_only), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())