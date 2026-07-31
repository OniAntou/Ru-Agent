from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.ru_v6_1_corpus import build_ru_v6_1_corpus
from mini_gpt.ru_v6_2_corpus import build_ru_v6_2_corpus
from mini_gpt.ru_v6_2_fresh_corpus import build_ru_v6_2_fresh_corpus
from mini_gpt.ru_v6_3_corpus import build_ru_v6_3_corpus

DATA_ROOT = PROJECT_ROOT / "data" / "conversations"
BASELINE_FILE = PROJECT_ROOT / "scripts" / "corpus_baseline_hashes.json"
SPLIT_FILES = ("train.jsonl", "val.jsonl", "held_out.jsonl", "manifest.json")

# NOTE: The gate is "refactor-neutral": it compares hashes of a fresh build
# against scripts/corpus_baseline_hashes.json, which was captured from the
# pre-refactor code. It does NOT compare against data/conversations/* because
# that data is a historical artifact that the current code cannot reproduce:
#   - v6_1 train has 1683 rows (1680 + 3 approved imports); the merge-import
#     code was removed from the repo but the data was never rebuilt.
#   - v6's VI_DEFAULT_VOICE check was added after the data was built and
#     rejects 14/160 Vietnamese rows ("một mình", "bực mình", "thằng bạn"...).
#   - v6_2 / v6_2_fresh / v6_3 differ for the same historical reasons.
# Regenerate the baseline with --regenerate-baseline after an intentional
# behavior change (then re-train or accept new data deliberately).
VERSIONS = {
    "v6_1": {"seed": 42},
    "v6_2": {"seed": 42},
    "v6_2_fresh": {"seed": 62},
    "v6_3": {"seed": 63},
}


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _build_into(version: str, destination: Path, seed: int) -> None:
    if version == "v6_1":
        build_ru_v6_1_corpus(destination, seed=seed)
    elif version == "v6_2":
        build_ru_v6_2_corpus(destination, seed=seed)
    elif version == "v6_2_fresh":
        build_ru_v6_2_fresh_corpus(
            destination,
            approved_file=DATA_ROOT / "approved.jsonl",
            memory_file=DATA_ROOT / "memory_texts.json",
            seed=seed,
        )
    elif version == "v6_3":
        build_ru_v6_3_corpus(destination, seed=seed)
    else:
        raise ValueError(f"unknown version: {version}")


def _collect_hashes(version: str, seed: int) -> dict[str, str]:
    with TemporaryDirectory(prefix=f"verify-{version}-") as temporary_dir:
        _build_into(version, Path(temporary_dir), seed)
        return {name: _sha256_file(Path(temporary_dir) / name) for name in SPLIT_FILES}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Verify rebuilt corpora match the pre-refactor baseline (refactor-neutral gate)")
    parser.add_argument("--version", choices=sorted(VERSIONS), action="append")
    parser.add_argument("--json", action="store_true", help="print machine-readable results")
    parser.add_argument(
        "--regenerate-baseline",
        action="store_true",
        help="capture current code output as the new baseline (use only for intentional behavior changes)",
    )
    args = parser.parse_args(argv)

    selected = sorted(args.version) if args.version else sorted(VERSIONS)

    if args.regenerate_baseline:
        baseline = {version: _collect_hashes(version, spec["seed"]) for version, spec in VERSIONS.items()}
        BASELINE_FILE.write_text(
            json.dumps(baseline, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(f"baseline regenerated for {len(baseline)} versions -> {BASELINE_FILE}")
        return 0

    if not BASELINE_FILE.is_file():
        print(f"ERROR: baseline file missing: {BASELINE_FILE}")
        print("Run once with --regenerate-baseline on the pre-refactor code to capture it.")
        return 1
    baseline = json.loads(BASELINE_FILE.read_text(encoding="utf-8"))

    results: dict[str, str] = {}
    failures: list[str] = []
    for version in selected:
        rebuilt = _collect_hashes(version, VERSIONS[version]["seed"])
        problems: list[str] = []
        for name in SPLIT_FILES:
            expected = baseline.get(version, {}).get(name)
            if expected is None:
                problems.append(f"{name}: no baseline hash recorded")
                continue
            if expected != rebuilt[name]:
                problems.append(f"{name}: hash mismatch (baseline={expected[:16]}... rebuilt={rebuilt[name][:16]}...)")
        status = "FAIL" if problems else "PASS"
        results[version] = status
        if status == "FAIL":
            failures.extend(f"{version}: {problem}" for problem in problems)
        if not args.json:
            print(f"[{status:4s}] {version}")
            for problem in problems:
                print(f"        {problem}")

    if args.json:
        print(json.dumps({"results": results, "failures": failures}, ensure_ascii=False, indent=2))
    if failures:
        print("REFACTOR-NEUTRAL FAILURES:")
        for failure in failures:
            print(f"  - {failure}")
        return 1
    print("REFACTOR-NEUTRAL-OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
