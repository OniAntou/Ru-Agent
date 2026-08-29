"""Inventory the Python runtime boundary without executing product code.

The inventory is intentionally filesystem-only.  It records enough provenance
to review the migration boundary while keeping output deterministic and free of
source text, credentials, or external-service responses.
"""

from __future__ import annotations

import argparse
import ast
import hashlib
import json
import sys
from collections.abc import Iterable, Sequence
from dataclasses import asdict, dataclass
from pathlib import Path, PurePosixPath

_SCAN_ROOTS = ("src/ru_runtime", "scripts", "tools", "tests", "backend-dotnet")
_IGNORED_PARTS = {"__pycache__", ".pytest_cache", "bin", "obj", ".git"}
_MAX_HASH_BYTES = 64 * 1024
_MAX_FILES = 20_000
_PYTHON_MODULE_ROOT = "src"
_PYTHON_WORKER_MODULE = "ru_runtime.worker.server"
_C_SHARP_STARTUP_FILES = (
    "backend-dotnet/RuBackend/Program.cs",
    "backend-dotnet/RuAgent.Hosting/WorkerBrokerLaunchPlan.cs",
)


@dataclass(frozen=True)
class RuntimeBoundaryRecord:
    path: str
    classification: str
    reachable_from_managed_startup: bool
    reason: str
    sha256: str
    size_bytes: int

    def to_payload(self) -> dict[str, object]:
        return asdict(self)


def classify(path: str) -> tuple[str, str]:
    """Return the migration class and stable explanation for *path*."""

    if path.startswith(("src/ru_runtime/worker/v2/", "src/ru_runtime/generation/")):
        return "production-to-port", "managed-agent-reference"
    if path.startswith((
        "src/ru_runtime/worker/v1/",
        "src/ru_runtime/voice/",
        "src/ru_runtime/speech/",
        "src/ru_runtime/vts/",
        "src/ru_runtime/observation/",
    )):
        return "provider-only", "phase-two-device-boundary"
    if path.startswith(("scripts/", "tools/", "tests/")):
        return "tooling", "non-product-build"
    return "legacy", "not-reachable-from-managed-startup"


def _relative_files(root: Path) -> tuple[Path, ...]:
    candidates: list[Path] = []
    for relative_root in _SCAN_ROOTS:
        directory = root / Path(*relative_root.split("/"))
        if not directory.is_dir():
            continue
        for path in directory.rglob("*"):
            if not path.is_file() or any(part in _IGNORED_PARTS for part in path.parts):
                continue
            # Source and metadata files are useful to boundary review.  Skip
            # large binary artifacts and build outputs by construction.
            if path.suffix.lower() in {".py", ".cs", ".csproj", ".json", ".md", ".bat", ".ps1", ".toml", ".txt"}:
                candidates.append(path)
    return tuple(sorted(candidates, key=lambda item: item.relative_to(root).as_posix()))[:_MAX_FILES]


def _python_module(path: str) -> str | None:
    if not path.startswith("src/") or not path.endswith(".py"):
        return None
    relative = PurePosixPath(path).relative_to(_PYTHON_MODULE_ROOT)
    parts = list(relative.parts)
    if parts[-1] == "__init__.py":
        parts.pop()
    else:
        parts[-1] = PurePosixPath(parts[-1]).stem
    return ".".join(parts)


def _module_path(module: str, paths_by_module: dict[str, str]) -> str | None:
    if module in paths_by_module:
        return paths_by_module[module]
    return paths_by_module.get(module + ".__init__")


def _imported_modules(path: Path) -> tuple[str, ...]:
    try:
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    except (OSError, SyntaxError, UnicodeDecodeError):
        return ()
    modules: set[str] = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            modules.update(alias.name for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module:
            modules.add(node.module)
    return tuple(sorted(modules))


def _managed_reachability(root: Path, paths: tuple[Path, ...]) -> dict[str, tuple[bool, str]]:
    path_by_relative = {path.relative_to(root).as_posix(): path for path in paths}
    module_to_path = {
        module: relative
        for relative in path_by_relative
        if (module := _python_module(relative)) is not None
    }
    reachable: dict[str, tuple[bool, str]] = {}

    # The managed startup explicitly starts the Python v1 worker by module
    # name.  This is a subprocess edge, not a Python import edge.
    startup_text = "\n".join(
        path_by_relative[relative].read_text(encoding="utf-8", errors="replace")
        for relative in _C_SHARP_STARTUP_FILES
        if relative in path_by_relative
    )
    if _PYTHON_WORKER_MODULE in startup_text:
        worker_path = _module_path(_PYTHON_WORKER_MODULE, module_to_path)
        if worker_path is not None:
            reachable[worker_path] = (True, "subprocess-python-worker")

    # Follow only explicit Python import edges from the started module.  The
    # resulting reason remains python-import and is useful for the migration
    # review, while managed C# files never receive that reason.
    pending = [path for path, (is_reachable, _) in reachable.items() if is_reachable]
    seen = set(pending)
    while pending:
        relative = pending.pop()
        path = path_by_relative.get(relative)
        if path is None:
            continue
        for module in _imported_modules(path):
            imported = _module_path(module, module_to_path)
            if imported is None or imported in seen:
                continue
            seen.add(imported)
            reachable[imported] = (True, "python-import")
            pending.append(imported)

    for relative in _C_SHARP_STARTUP_FILES:
        if relative in path_by_relative:
            reachable[relative] = (True, "managed-startup")
    return reachable


def _bounded_sha256(path: Path) -> tuple[str, int]:
    data = path.read_bytes()
    return hashlib.sha256(data[:_MAX_HASH_BYTES]).hexdigest(), len(data)


def scan_runtime_boundary(root: Path) -> tuple[RuntimeBoundaryRecord, ...]:
    """Return a stable, bounded inventory rooted at *root*."""

    root = root.resolve()
    paths = _relative_files(root)
    reachable = _managed_reachability(root, paths)
    records: list[RuntimeBoundaryRecord] = []
    for path in paths:
        relative = path.relative_to(root).as_posix()
        classification, reason = classify(relative)
        is_reachable, reach_reason = reachable.get(relative, (False, reason))
        if is_reachable:
            reason = reach_reason
        sha256, size_bytes = _bounded_sha256(path)
        records.append(
            RuntimeBoundaryRecord(
                path=relative,
                classification=classification,
                reachable_from_managed_startup=is_reachable,
                reason=reason,
                sha256=sha256,
                size_bytes=size_bytes,
            )
        )
    return tuple(records)


def _reference_cases() -> list[dict[str, object]]:
    """Return deterministic, bounded Phase 1 reference cases."""

    return [
        {
            "id": "prompt.basic",
            "group": "prompt",
            "input": {"schema_version": 1, "agent_profile": "ru-agent", "preset": "standard"},
            "expected": {"composer_version": "1.0.0"},
        },
        {
            "id": "compaction.basic",
            "group": "compaction",
            "input": {"message_count": 4, "budget_bytes": 4096},
            "expected": {"bounded": True},
        },
        {
            "id": "tool_selection.valid",
            "group": "tool_selection",
            "input": {"tool_id": "files.read", "tool_version": "1.0.0", "arguments": {}},
            "expected": {"accepted": True},
        },
        {
            "id": "protocol.unknown_type",
            "group": "protocol",
            "input": {"code": "unknown_type"},
            "expected": {"retryable": False},
        },
        {
            "id": "cancellation.before_dispatch",
            "group": "cancellation",
            "input": {"cancelled": True},
            "expected": {"completed": False},
        },
        {
            "id": "privacy.redaction",
            "group": "privacy",
            "input": {"prompt_text": "<redacted>", "credential": "<redacted>"},
            "expected": {"prompt_text_visible": False, "credential_visible": False},
        },
    ]


def write_baseline_fixture(root: Path, destination: Path) -> None:
    """Write the deterministic Phase 1 reference fixture."""

    # Keep the writer rooted in the supplied project only to make accidental
    # external reads impossible; the cases themselves are pure constants.
    root = root.resolve()
    if not root.is_dir():
        raise ValueError("root must be a directory")
    destination = destination.resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    payload = {"schema_version": 1, "cases": _reference_cases()}
    destination.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )


def _summary(records: Iterable[RuntimeBoundaryRecord]) -> dict[str, object]:
    records = tuple(records)
    counts: dict[str, int] = {}
    for item in records:
        counts[item.classification] = counts.get(item.classification, 0) + 1
    return {
        "schema_version": 1,
        "file_count": len(records),
        "reachable_count": sum(item.reachable_from_managed_startup for item in records),
        "classification_counts": dict(sorted(counts.items())),
    }


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--write-baseline", type=Path)
    args = parser.parse_args(argv)
    records = scan_runtime_boundary(args.root)
    if args.write_baseline is not None:
        write_baseline_fixture(args.root, args.write_baseline)
    print(json.dumps(_summary(records), sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
