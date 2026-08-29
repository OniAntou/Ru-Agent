"""Phase 1 runtime measurement gates for the deterministic Python paths."""

from __future__ import annotations

import json
from pathlib import Path

from scripts.measure_phase1_runtime import (
    Phase1MeasurementRecord,
    check_regression,
    measure_runtime,
)

ROOT = Path(__file__).resolve().parents[1]
FIXTURE_PATH = ROOT / "tests/fixtures/python-csharp-phase1-baseline.json"
EXPECTED_OPERATIONS = {"prompt.compose", "tool_selection.parse"}


def load_fixture() -> dict:
    return json.loads(FIXTURE_PATH.read_text(encoding="utf-8"))


def test_measurements_are_bounded_ordered_and_complete() -> None:
    fixture = load_fixture()
    min_samples = int(fixture["measurement"]["min_samples"])

    # A larger batch keeps the tiny sub-millisecond p50 stable across CI
    # scheduler jitter while preserving the ten-percent regression gate.
    records = measure_runtime(ROOT, samples=min_samples * 3)

    assert {record.operation for record in records} == EXPECTED_OPERATIONS
    for record in records:
        assert record.samples >= min_samples
        assert 0.0 <= record.p50_ms <= record.p95_ms
        assert record.peak_bytes > 0
        assert record.to_payload()["operation"] == record.operation


def test_measurements_stay_within_the_regression_tolerance() -> None:
    fixture = load_fixture()
    min_samples = int(fixture["measurement"]["min_samples"])

    # A larger batch keeps the tiny sub-millisecond p50 stable across CI
    # scheduler jitter while preserving the ten-percent regression gate.
    records = measure_runtime(ROOT, samples=min_samples * 3)

    assert check_regression(records, fixture) == []


def test_regression_gate_rejects_a_material_timing_regression() -> None:
    fixture = load_fixture()
    baseline = fixture["measurement"]["baseline"]
    records = tuple(
        Phase1MeasurementRecord(
            operation=name,
            samples=20,
            p50_ms=float(reference["p50_ms"]) * 20.0,
            p95_ms=float(reference["p95_ms"]) * 20.0,
            peak_bytes=int(reference["peak_bytes"]),
        )
        for name, reference in baseline.items()
    )

    failures = check_regression(records, fixture)

    assert any("p50 regressed" in failure for failure in failures)
    assert any("p95 regressed" in failure for failure in failures)
