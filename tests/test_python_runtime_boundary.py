from __future__ import annotations

import json
from pathlib import Path

from scripts.check_python_runtime_boundary import _reference_cases, scan_runtime_boundary

ROOT = Path(__file__).resolve().parents[1]


def test_current_worker_modules_are_classified_by_runtime_boundary() -> None:
    records = {item.path: item for item in scan_runtime_boundary(ROOT)}

    # Phase 3: generation and worker/v2 are retired (C# owns them); they are
    # no longer present and must not be reported as production-to-port reachable.
    # The inventory should either not contain them or mark them as not reachable.
    for retired in (
        "src/ru_runtime/worker/v2/model_step.py",
        "src/ru_runtime/worker/v1/voice.py",
    ):
        if retired in records:
            assert records[retired].classification in ("production-to-port", "provider-only", "legacy")
            assert records[retired].reachable_from_managed_startup is False
        else:
            # Retired module correctly absent from inventory
            assert retired not in records
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


def test_phase_one_reference_cases_still_build_after_generation_retirement() -> None:
    cases = _reference_cases(ROOT)
    assert {case["id"] for case in cases} == {
        "prompt.basic",
        "compaction.basic",
        "tool_selection.valid",
        "protocol.unknown_type",
        "cancellation.before_dispatch",
        "privacy.redaction",
    }
