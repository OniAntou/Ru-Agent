"""Inventory the Python runtime boundary without executing product code.

The inventory is intentionally filesystem-only.  It records enough provenance
to review the migration boundary while keeping output deterministic and free of
source text, credentials, or external-service responses. Known entrypoints are
labels, not a dependency graph or proof of production reachability.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import sys
from collections.abc import Callable, Iterable, Sequence
from dataclasses import asdict, dataclass
from pathlib import Path

_SCAN_ROOTS = ("src/ru_runtime", "scripts", "tools", "tests", "backend-dotnet")
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
    """Return the migration class and stable explanation for *path*."""

    if path in _C_SHARP_STARTUP_FILES:
        return "managed-startup", "known-entrypoint"
    if path.startswith(("src/ru_runtime/worker/v2/", "src/ru_runtime/generation/")):
        return "production-to-port", "managed-agent-reference"
    if path.startswith((
        "src/ru_runtime/worker/v1/",
        "src/ru_runtime/voice/",
        "src/ru_runtime/speech/",
        "src/ru_runtime/observation/",
    )):
        return "provider-only", "phase-two-device-boundary"
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
            # Do not hash or parse a symlink/reparse target outside the
            # requested root.  ``Path.rglob`` yields the lexical path, so an
            # explicit resolved-root check is required for boundary safety.
            try:
                if not path.resolve().is_relative_to(root):
                    continue
            except (OSError, RuntimeError):
                continue
            # Source and metadata files are useful to boundary review.  Skip
            # large binary artifacts and build outputs by construction.
            if path.suffix.lower() in {".py", ".cs", ".csproj", ".json", ".md", ".bat", ".ps1", ".toml", ".txt"}:
                candidates.append(path)
    return tuple(sorted(candidates, key=lambda item: item.relative_to(root).as_posix()))[:_MAX_FILES]


def _bounded_sha256(path: Path) -> tuple[str, int]:
    with path.open("rb") as handle:
        data = handle.read(_MAX_HASH_BYTES)
    return hashlib.sha256(data).hexdigest(), path.stat().st_size


def scan_runtime_boundary(root: Path) -> tuple[RuntimeBoundaryRecord, ...]:
    """Return a stable, bounded inventory rooted at *root*."""

    root = root.resolve()
    paths = _relative_files(root)
    records: list[RuntimeBoundaryRecord] = []
    for path in paths:
        relative = path.relative_to(root).as_posix()
        classification, reason = classify(relative)
        sha256, size_bytes = _bounded_sha256(path)
        records.append(
            RuntimeBoundaryRecord(
                path=relative,
                classification=classification,
                is_known_managed_startup=relative in _C_SHARP_STARTUP_FILES,
                reason=reason,
                sha256=sha256,
                size_bytes=size_bytes,
            )
        )
    return tuple(records)


def _reference_functions(root: Path) -> tuple[
    Callable[..., object],
    Callable[[object], object],
    Callable[..., str],
    Callable[[str], str],
    int,
]:
    """Import the deterministic Python reference composers used for golden values.

    Only ``write_baseline_fixture`` may call this; the inventory itself stays
    filesystem-only.
    """

    scripts = root / "scripts"
    src = root / "src"
    if not src.is_dir() or not scripts.is_dir():
        raise ValueError("root must contain src and scripts directories for the reference fixture")
    for path in (scripts, src):
        if str(path) not in sys.path:
            sys.path.insert(0, str(path))
    # Production generation is owned by C# after Phase 3.  Baseline generation
    # remains available through this test-only copy so the scanner itself never
    # imports retired product modules.
    from harness.generation_stub.prompt_context import (
        MAX_PROMPT_INSTRUCTION_BYTES,
        normalize_text,
        parse_system_prompt_context,
        sha256_utf8,
    )
    from harness.generation_stub.system_prompt_composer import compose_system_prompt

    return (
        compose_system_prompt,
        parse_system_prompt_context,
        normalize_text,
        sha256_utf8,
        MAX_PROMPT_INSTRUCTION_BYTES,
    )


_PRIVATE_MARKER = "sk-PHASE1-PRIVATE-MARKER"
_INSTRUCTION_CONTENT = "Root guidance for the reference context.\nLine two."


def _reference_prompt_input(
    normalize_text: Callable[..., str],
    sha256_utf8: Callable[[str], str],
    max_instruction_bytes: int,
) -> dict[str, object]:
    """Build the one golden prompt context shared by every Phase 1 consumer."""

    return {
        "schema_version": 1,
        "agent_profile": "ru-agent",
        "preset": "standard",
        "environment": {
            "model": "phase1-reference-model",
            "platform": "windows",
            "locale": "vi-VN",
            "timezone": "Asia/Bangkok",
            "date": "2026-08-29",
            "workspace_label": "LLM",
        },
        "instruction_blocks": [
            {
                "source_id": "root:Ru.md",
                "display_name": "Ru.md",
                "content": _INSTRUCTION_CONTENT,
                "content_sha256": sha256_utf8(
                    normalize_text(_INSTRUCTION_CONTENT, maximum_bytes=max_instruction_bytes)
                ),
            }
        ],
        "capabilities": [
            {
                "id": "files.read",
                "version": "1.2.0",
                "summary": "Read workspace files with bounded output",
                "input_schema_digest": sha256_utf8("files.read schema"),
                "policy_summary": "workspace.read",
                "availability": "enabled",
            },
            {
                "id": "web.search",
                "version": "1.0.0",
                "summary": "Search the hosted web boundary",
                "input_schema_digest": sha256_utf8("web.search schema"),
                "policy_summary": "web.search",
                "availability": "disabled",
            },
        ],
        "runtime_context": {
            "memory": "User prefers concise answers.",
            "mood": None,
            "voice": None,
            "vts": None,
            "hardware": None,
            "references": [
                {
                    "kind": "doc",
                    "label": "Phase 1 note",
                    "content": "Reference material must stay untrusted.",
                }
            ],
        },
    }


def _reference_cases(root: Path) -> list[dict[str, object]]:
    """Return deterministic, bounded Phase 1 reference cases with golden values."""

    (
        compose_system_prompt,
        parse_system_prompt_context,
        normalize_text,
        sha256_utf8,
        max_instruction_bytes,
    ) = _reference_functions(root)
    prompt_input = _reference_prompt_input(normalize_text, sha256_utf8, max_instruction_bytes)
    snapshot = compose_system_prompt(parse_system_prompt_context(prompt_input))
    tool_arguments = {"path": "README.md", "limit": 64}
    tool_text = json.dumps(
        {"tool_id": "files.read", "tool_version": "1.0.0", "arguments": tool_arguments},
        ensure_ascii=False,
        separators=(",", ":"),
    )

    return [
        {
            "id": "prompt.basic",
            "group": "prompt",
            "input": prompt_input,
            "expected": {
                "composer_version": snapshot.receipt.composer_version,
                "prompt_sha256": snapshot.receipt.prompt_sha256,
                "block_digests": [
                    {"block_id": block.block_id, "sha256": block.sha256}
                    for block in snapshot.receipt.block_digests
                ],
                "private_marker": _PRIVATE_MARKER,
            },
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
            "input": {
                "text": tool_text,
                "tool_id": "files.read",
                "tool_version": "1.0.0",
                "arguments": tool_arguments,
                "advertised": ["files.read", "terminal.run"],
            },
            "expected": {"accepted": True},
        },
        {
            "id": "protocol.unknown_type",
            "group": "protocol",
            "input": {
                "envelope": {"v": 1, "id": "phase1-probe", "type": "agent.unknown", "payload": {}},
            },
            "expected": {"code": "unknown_type", "retryable": False},
        },
        {
            "id": "cancellation.before_dispatch",
            "group": "cancellation",
            "input": {"prompt": "xin chào anh", "cancelled": True},
            "expected": {"completed": False},
        },
        {
            "id": "privacy.redaction",
            "group": "privacy",
            "input": {
                "prompt_text": "PRIVATE-PROMPT-9f1c",
                "credential": "sk-PRIVATE-CREDENTIAL-77ae",
            },
            "expected": {"prompt_text_visible": False, "credential_visible": False},
        },
    ]


def _measurement_section(root: Path, cases: list[dict[str, object]]) -> dict[str, object]:
    """Measure the deterministic baseline recorded next to the reference cases."""

    measurement_module_path = root / "scripts" / "measure_phase1_runtime.py"
    if not measurement_module_path.is_file():
        raise ValueError("root must contain scripts/measure_phase1_runtime.py")
    module_spec = importlib.util.spec_from_file_location(
        "ru_phase1_measurement_reference",
        measurement_module_path,
    )
    if module_spec is None or module_spec.loader is None:
        raise ValueError("could not load the Phase 1 measurement helper")
    module = importlib.util.module_from_spec(module_spec)
    sys.modules[module_spec.name] = module
    try:
        module_spec.loader.exec_module(module)
    finally:
        sys.modules.pop(module_spec.name, None)
    MIN_SAMPLES = module.MIN_SAMPLES
    measure_runtime = module.measure_runtime

    prompt_input = next(case["input"] for case in cases if case["id"] == "prompt.basic")
    tool_case = next(case for case in cases if case["id"] == "tool_selection.valid")
    records = measure_runtime(
        root,
        MIN_SAMPLES,
        prompt_input=prompt_input,
        tool_selection_text=str(tool_case["input"]["text"]),
    )
    return {
        "min_samples": MIN_SAMPLES,
        "regression_tolerance": 0.10,
        "baseline": {
            record.operation: {
                "p50_ms": record.p50_ms,
                "p95_ms": record.p95_ms,
                "peak_bytes": record.peak_bytes,
            }
            for record in records
        },
    }


def write_baseline_fixture(root: Path, destination: Path) -> None:
    """Write the deterministic Phase 1 reference fixture."""

    # Keep the writer rooted in the supplied project only to make accidental
    # external reads impossible; the cases themselves are pure constants.
    root = root.resolve()
    if not root.is_dir():
        raise ValueError("root must be a directory")
    destination = destination.resolve()
    if destination == root or not destination.is_relative_to(root):
        raise ValueError("baseline destination must be below root")
    destination.parent.mkdir(parents=True, exist_ok=True)
    cases = _reference_cases(root)
    payload = {
        "schema_version": 1,
        "cases": cases,
        "measurement": _measurement_section(root, cases),
    }
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
        "schema_version": 2,
        "file_count": len(records),
        "known_managed_startup_count": sum(item.is_known_managed_startup for item in records),
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
