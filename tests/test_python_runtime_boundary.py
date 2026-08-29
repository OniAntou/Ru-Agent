from __future__ import annotations

import json
from pathlib import Path

from scripts.check_python_runtime_boundary import RuntimeBoundaryRecord, scan_runtime_boundary

ROOT = Path(__file__).resolve().parents[1]


def test_current_worker_modules_are_classified_by_runtime_boundary() -> None:
    records = {item.path: item for item in scan_runtime_boundary(ROOT)}

    assert isinstance(records["src/ru_runtime/worker/v2/model_step.py"], RuntimeBoundaryRecord)
    assert records["src/ru_runtime/worker/v2/model_step.py"].classification == "production-to-port"
    assert records["src/ru_runtime/worker/v1/voice.py"].classification == "provider-only"
    assert records["scripts/benchmark_tts.py"].classification == "tooling"
    legacy_path = "src/ru_runtime/ru_v6_3_corpus.py"
    if legacy_path in records:
        assert records[legacy_path].classification == "legacy"
    else:
        # The corpus module was already retired from this checkout; the
        # inventory must not resurrect a deleted artifact just to classify it.
        assert records["src/ru_runtime/reference_text.txt"].classification == "legacy"


def test_managed_startup_has_no_python_import_edge() -> None:
    records = scan_runtime_boundary(ROOT)
    assert not any(
        item.path.startswith("backend-dotnet/")
        and item.reachable_from_managed_startup
        and item.reason == "python-import"
        for item in records
    )


def test_baseline_fixture_has_all_phase_one_reference_groups() -> None:
    fixture = json.loads(
        (ROOT / "tests/fixtures/python-csharp-phase1-baseline.json").read_text(encoding="utf-8")
    )
    assert fixture["schema_version"] == 1
    assert {case["group"] for case in fixture["cases"]} == {
        "prompt",
        "compaction",
        "tool_selection",
        "protocol",
        "cancellation",
        "privacy",
    }
