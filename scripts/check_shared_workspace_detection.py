"""Deterministic shared-workspace detection gate.

This gate intentionally models filesystem mutations in a temporary directory;
it does not identify the external process. It verifies the contract fixture,
hash evidence, event ordering, and binary fail-closed policy without touching a
user workspace or live audio/desktop services.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import tempfile
from pathlib import Path
from typing import Any


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


# The fixture's `expected` is an event vocabulary, not free text: a typo (or a
# changed name) must fail the gate instead of silently skipping the property it
# was meant to exercise (the binary-conflict fail-closed branch in particular).
KNOWN_EVENTS = frozenset({
    "workspace_file_changed",
    "workspace_external_change_detected",
    "workspace_undeclared_text_discovered",
    "workspace_claim_orphaned",
    "workspace_recovery_reconciled",
    "last_writer_wins",
    "concurrent_binary_conflict",
})


def evaluate_scenario(scenario: dict[str, Any], root: Path, sequence: int) -> dict[str, Any]:
    path = scenario["path"]
    expected = scenario["expected"]
    if expected not in KNOWN_EVENTS:
        raise KeyError(f"unknown expected event {expected!r}")
    target = root / path
    target.parent.mkdir(parents=True, exist_ok=True)
    before = b"base"
    target.write_bytes(before)
    observed = b"external" if scenario["writer"] == "unknown_external" else b"peer"
    # Binary conflicts are fail-closed: the candidate bytes are observed but
    # never replace the working file.
    if expected != "concurrent_binary_conflict":
        target.write_bytes(observed)
    result = {
        "name": scenario["name"],
        "writer_kind": scenario["writer"],
        "path": path,
        "before_sha256": sha256(before),
        "observed_sha256": sha256(observed),
        "sequence": sequence,
        "event": expected,
    }
    if result["before_sha256"] == result["observed_sha256"]:
        raise AssertionError(f"no authoritative change for {scenario['name']}")
    if expected == "concurrent_binary_conflict" and before != target.read_bytes():
        raise AssertionError("binary conflict modified the working file")
    return result


def run_gate(fixture: Path) -> dict[str, Any]:
    scenarios = json.loads(fixture.read_text(encoding="utf-8"))
    if not isinstance(scenarios, list) or not scenarios:
        raise ValueError("shared workspace fixture must be a non-empty list")
    with tempfile.TemporaryDirectory(prefix="ru-shared-workspace-gate-") as temp:
        root = Path(temp)
        results: list[dict[str, Any]] = []
        failed = 0
        for index, item in enumerate(scenarios, 1):
            try:
                results.append(evaluate_scenario(item, root, index))
            except (AssertionError, KeyError, OSError) as error:
                failed += 1
                name = item.get("name") if isinstance(item, dict) else None
                results.append({"name": name, "error": str(error)})
    sequences = [item["sequence"] for item in results if "sequence" in item]
    if sequences != sorted(set(sequences)):
        raise AssertionError("coordinator sequences are not strictly monotonic")
    if any(
        item["writer_kind"] != "unknown_external"
        for item in results
        if "writer_kind" in item and ("codex" in (item["name"] or "") or "opencode" in (item["name"] or ""))
    ):
        raise AssertionError("external identity leaked into writer classification")
    return {"scenarios": len(results), "failed": failed, "results": results}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        summary = run_gate(args.fixture.resolve())
    except (OSError, ValueError, AssertionError, KeyError) as error:
        print(json.dumps({"scenarios": 0, "failed": 1, "error": str(error)}, ensure_ascii=False))
        return 1
    print(json.dumps(summary, ensure_ascii=False, sort_keys=True))
    # A scenario that failed inside run_gate is still a failed gate: reporting the
    # summary is not enough when automation only sees the exit code.
    return 1 if summary["failed"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
