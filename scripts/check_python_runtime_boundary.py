"""Inventory the retained Python source boundary without executing product code."""

from __future__ import annotations

import argparse
import hashlib
import json
from collections.abc import Iterable, Sequence
from dataclasses import asdict, dataclass
from pathlib import Path

_SCAN_ROOTS = ("scripts", "tools", "tests", "backend-dotnet")
_IGNORED_PARTS = {"__pycache__", ".pytest_cache", "bin", "obj", ".git"}
_MAX_HASH_BYTES = 64 * 1024
_MAX_FILES = 20_000
_C_SHARP_STARTUP_FILES = (
    "backend-dotnet/RuBackend/Program.cs",
    "backend-dotnet/RuAgent.Hosting/WorkerBrokerLaunchPlan.cs",
)


@dataclass(frozen=True)
class RuntimeBoundaryRecord:
    path: str
    classification: str
    is_known_managed_startup: bool
    reason: str
    sha256: str
    size_bytes: int

    def to_payload(self) -> dict[str, object]:
        return asdict(self)


def classify(path: str) -> tuple[str, str]:
    if path in _C_SHARP_STARTUP_FILES:
        return "managed-startup", "known-entrypoint"
    if path.startswith(("scripts/", "tools/", "tests/")):
        return "tooling", "non-product-build"
    return "legacy", "outside-classified-source-groups"


def _relative_files(root: Path) -> tuple[Path, ...]:
    candidates: list[Path] = []
    for relative_root in _SCAN_ROOTS:
        directory = root / Path(*relative_root.split("/"))
        if not directory.is_dir():
            continue
        for path in directory.rglob("*"):
            if not path.is_file() or any(part in _IGNORED_PARTS for part in path.parts):
                continue
            try:
                if not path.resolve().is_relative_to(root):
                    continue
            except (OSError, RuntimeError):
                continue
            if path.suffix.lower() in {
                ".py", ".cs", ".csproj", ".json", ".md", ".bat", ".ps1", ".toml", ".txt",
            }:
                candidates.append(path)
    return tuple(sorted(candidates, key=lambda item: item.relative_to(root).as_posix()))[:_MAX_FILES]


def _bounded_sha256(path: Path) -> tuple[str, int]:
    with path.open("rb") as handle:
        data = handle.read(_MAX_HASH_BYTES)
    return hashlib.sha256(data).hexdigest(), path.stat().st_size


def scan_runtime_boundary(root: Path) -> tuple[RuntimeBoundaryRecord, ...]:
    root = root.resolve()
    records: list[RuntimeBoundaryRecord] = []
    for path in _relative_files(root):
        relative = path.relative_to(root).as_posix()
        classification, reason = classify(relative)
        sha256, size_bytes = _bounded_sha256(path)
        records.append(RuntimeBoundaryRecord(
            path=relative,
            classification=classification,
            is_known_managed_startup=relative in _C_SHARP_STARTUP_FILES,
            reason=reason,
            sha256=sha256,
            size_bytes=size_bytes,
        ))
    return tuple(records)


def _summary(records: Iterable[RuntimeBoundaryRecord]) -> dict[str, object]:
    records = tuple(records)
    counts: dict[str, int] = {}
    for item in records:
        counts[item.classification] = counts.get(item.classification, 0) + 1
    return {
        "schema_version": 2,
        "file_count": len(records),
        "known_managed_startup_count": sum(item.is_known_managed_startup for item in records),
        "classification_counts": dict(sorted(counts.items())),
    }


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument(
        "--fail-on",
        action="append",
        default=[],
        help="Fail (exit 1) when any file has one of these classifications. "
        "Repeatable. Default: never fail; print the summary and exit 0.",
    )
    args = parser.parse_args(argv)
    records = scan_runtime_boundary(args.root)
    print(json.dumps(_summary(records), sort_keys=True))
    if args.fail_on:
        hits = sorted({r.path for r in records if r.classification in args.fail_on})
        if hits:
            print(json.dumps({"fail_on": sorted(args.fail_on), "hits": hits[:50], "hit_count": len(hits)}, sort_keys=True))
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
