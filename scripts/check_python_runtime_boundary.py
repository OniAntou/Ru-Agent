"""Inventory the retained Python source boundary without executing product code."""

from __future__ import annotations

import argparse
import hashlib
import json
from collections.abc import Iterable, Sequence
from dataclasses import asdict, dataclass
from pathlib import Path

_SCAN_ROOTS = ("scripts", "tools", "tests", "backend-dotnet", "gui-winui3", "gui-winui3.Tests", "gui-winui3.Harness")
_IGNORED_PARTS = {"__pycache__", ".pytest_cache", "bin", "obj", ".git", ".venv", "node_modules"}
_MAX_HASH_BYTES = 64 * 1024
_MAX_FILES = 20_000
_C_SHARP_STARTUP_FILES = (
    "backend-dotnet/RuBackend/Program.cs",
    "backend-dotnet/RuBackend/Hosting/InProcessExecutionAuthority.cs",
)
# The product is .NET only: no Python, Node, or other interpreter ships or runs inside
# it. These roots are the product trees; `scripts`, `tools`, and `tests` are repository
# tooling and are expected to hold Python.
_PRODUCT_ROOTS = ("backend-dotnet/", "gui-winui3/", "gui-winui3.Tests/", "gui-winui3.Harness/")
_INTERPRETER_SUFFIXES = (".py", ".pyw", ".js", ".mjs", ".cjs", ".ts", ".rb", ".pl", ".lua")
# The GUI's interactive terminal vendors xterm.js (MIT) for WebView2 to parse.
# A browser library executed by a Windows component is not a shipped interpreter,
# which is what this scanner exists to catch, and `InteractiveTerminalContractTests`
# pins both files. The allowlist is exact paths, lowercase, so any other `.js`
# under a product root still fails the check.
_VENDORED_BROWSER_ASSETS = frozenset(
    {
        "gui-winui3/assets/terminal/addon-fit.js",
        "gui-winui3/assets/terminal/xterm.js",
    }
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


def _relative_files(root: Path) -> tuple[tuple[Path, ...], bool]:
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
                # Interpreter files are inventoried so the product-tree boundary can be
                # checked: a `.js` under backend-dotnet is the regression, and a scanner
                # that never looks at the suffix cannot see it.
                *_INTERPRETER_SUFFIXES,
            }:
                candidates.append(path)
    ordered = tuple(sorted(candidates, key=lambda item: item.relative_to(root).as_posix()))
    return ordered[:_MAX_FILES], len(ordered) > _MAX_FILES


def _bounded_sha256(path: Path) -> tuple[str, int]:
    with path.open("rb") as handle:
        data = handle.read(_MAX_HASH_BYTES)
    return hashlib.sha256(data).hexdigest(), path.stat().st_size


def _scan_runtime_boundary(root: Path) -> tuple[tuple[RuntimeBoundaryRecord, ...], bool]:
    root = root.resolve()
    paths, truncated = _relative_files(root)
    records: list[RuntimeBoundaryRecord] = []
    for path in paths:
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
    return tuple(records), truncated


def scan_runtime_boundary(root: Path) -> tuple[RuntimeBoundaryRecord, ...]:
    records, _ = _scan_runtime_boundary(root)
    return records


def interpreter_files_in_product(records: Iterable[RuntimeBoundaryRecord]) -> list[str]:
    """Product-tree files that hand work to an interpreter.

    The repository rule is that Python, Node, and every other interpreter are tooling
    only: they may live under `scripts`, `tools`, and `tests`, and never inside the
    shipped product. This is the check that fails when one appears there. The
    vendored browser assets in :data:`_VENDORED_BROWSER_ASSETS` are the pinned
    exception: they are parsed by WebView2, not shipped as a runtime.
    """
    return sorted(
        record.path
        for record in records
        if record.path.startswith(_PRODUCT_ROOTS)
        and record.path.lower().endswith(_INTERPRETER_SUFFIXES)
        and record.path.lower() not in _VENDORED_BROWSER_ASSETS
    )


def _summary(
    records: Iterable[RuntimeBoundaryRecord], *, truncated: bool = False
) -> dict[str, object]:
    records = tuple(records)
    counts: dict[str, int] = {}
    for item in records:
        counts[item.classification] = counts.get(item.classification, 0) + 1
    return {
        "schema_version": 2,
        "file_count": len(records),
        "truncated": truncated,
        "known_managed_startup_count": sum(item.is_known_managed_startup for item in records),
        "classification_counts": dict(sorted(counts.items())),
        "interpreter_files_in_product_count": len(interpreter_files_in_product(records)),
    }


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument(
        "--fail-on",
        action="append",
        default=[],
        help="Fail (exit 1) when any file has one of these classifications. "
        "Repeatable.",
    )
    args = parser.parse_args(argv)
    records, truncated = _scan_runtime_boundary(args.root)
    print(json.dumps(_summary(records, truncated=truncated), sort_keys=True))
    failed = truncated
    if truncated:
        # A silently capped inventory claims completeness it does not have; the
        # cap is high enough that hitting it means the scan configuration is wrong.
        print(json.dumps(
            {"truncated": True, "file_cap": _MAX_FILES},
            sort_keys=True,
        ))
    # The interpreter boundary is always enforced: it is the one property of this
    # inventory that must hold, and a check nobody runs is not a check.
    interpreters = interpreter_files_in_product(records)
    if interpreters:
        print(json.dumps(
            {"interpreter_files_in_product": interpreters[:50], "hit_count": len(interpreters)},
            sort_keys=True,
        ))
        failed = True
    if args.fail_on:
        hits = sorted({r.path for r in records if r.classification in args.fail_on})
        if hits:
            print(json.dumps({"fail_on": sorted(args.fail_on), "hits": hits[:50], "hit_count": len(hits)}, sort_keys=True))
            failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
