from __future__ import annotations

from pathlib import Path

from benchmark_backend import percentile, summarize_samples

ROOT = Path(__file__).resolve().parents[1]


def test_percentile_uses_linear_interpolation() -> None:
    assert percentile([10.0, 20.0, 30.0, 40.0], 50) == 25.0
    assert percentile([10.0, 20.0, 30.0, 40.0], 95) == 38.5


def test_empty_summary_is_explicit_and_failure_count_is_preserved() -> None:
    assert summarize_samples([], failures=2) == {
        "sample_count": 0,
        "failures": 2,
        "p50_ms": None,
        "p95_ms": None,
    }


def test_harness_has_only_live_modes() -> None:
    current = ROOT / "scripts" / "ru_harness.py"
    assert current.is_file()
    retired_name = "aspnetcore_" + "compatibility_harness.py"
    assert not (ROOT / "scripts" / retired_name).exists()
    source = current.read_text(encoding="utf-8")
    assert 'choices=("backend-deterministic", "gui-deterministic")' in source
    assert 'default="backend-deterministic"' in source
    assert "runtime-" + "live" not in source


def test_backend_benchmark_has_current_name_only() -> None:
    assert (ROOT / "scripts" / "benchmark_backend.py").is_file()
    retired_name = "benchmark_aspnetcore_" + "compat.py"
    assert not (ROOT / "scripts" / retired_name).exists()
