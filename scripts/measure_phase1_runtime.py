"""Measure deterministic Phase 1 Python reference operations.

Only deterministic reference functions run here: no models, network, audio
devices, or VTube Studio state are contacted.  Wall-clock timing amortizes the
timer and scheduler cost over a small batch; allocation peaks are sampled in a
separate pass with ``tracemalloc``.
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
import time
import tracemalloc
from collections.abc import Callable, Mapping, Sequence
from dataclasses import asdict, dataclass
from pathlib import Path

MIN_SAMPLES = 20
REGRESSION_TOLERANCE = 0.10
# Even with batched samples, Windows timer and scheduler noise can add a few
# dozen microseconds to a sub-millisecond operation. Keep the relative
# ten-percent gate, but ignore this bounded absolute jitter as well.
TIMING_JITTER_FLOOR_MS = 0.05
# A larger batch keeps sub-millisecond timings meaningful when the host is
# busy. The reported duration is still per invocation, so this does not
# change the regression contract for the reference operation itself.
TIMING_BATCH_SIZE = 256
OPERATIONS = ("prompt.compose", "tool_selection.parse")
FIXTURE_RELATIVE_PATH = "tests/fixtures/python-csharp-phase1-baseline.json"


@dataclass(frozen=True)
class Phase1MeasurementRecord:
    operation: str
    samples: int
    p50_ms: float
    p95_ms: float
    peak_bytes: int

    def to_payload(self) -> dict[str, object]:
        return asdict(self)


def _import_reference(
    root: Path,
) -> tuple[Callable[[object], object], Callable[[object], object], Callable[[str], object]]:
    src = root / "src"
    if src.is_dir() and str(src) not in sys.path:
        sys.path.insert(0, str(src))
    from ru_runtime.generation.prompt_context import parse_system_prompt_context
    from ru_runtime.generation.system_prompt_composer import compose_system_prompt
    from ru_runtime.generation.tool_calls import parse_tool_call

    return compose_system_prompt, parse_system_prompt_context, parse_tool_call


def _load_fixture_inputs(root: Path) -> tuple[dict[str, object], str]:
    fixture = json.loads((root / FIXTURE_RELATIVE_PATH).read_text(encoding="utf-8"))
    prompt_case = next(item for item in fixture["cases"] if item["id"] == "prompt.basic")
    tool_case = next(item for item in fixture["cases"] if item["id"] == "tool_selection.valid")
    return dict(prompt_case["input"]), str(tool_case["input"]["text"])


def measure_runtime(
    root: Path,
    samples: int = MIN_SAMPLES,
    *,
    prompt_input: Mapping[str, object] | None = None,
    tool_selection_text: str | None = None,
) -> tuple[Phase1MeasurementRecord, ...]:
    """Measure the deterministic Phase 1 reference operations."""

    root = root.resolve()
    if not root.is_dir():
        raise ValueError("root must be a directory")
    samples = max(MIN_SAMPLES, samples)
    if prompt_input is None or tool_selection_text is None:
        fixture_prompt, fixture_tool = _load_fixture_inputs(root)
        prompt_input = prompt_input if prompt_input is not None else fixture_prompt
        tool_selection_text = tool_selection_text if tool_selection_text is not None else fixture_tool

    compose_system_prompt, parse_system_prompt_context, parse_tool_call = _import_reference(root)
    context = parse_system_prompt_context(prompt_input)
    operations: dict[str, Callable[[], object]] = {
        "prompt.compose": lambda: compose_system_prompt(context),
        "tool_selection.parse": lambda: parse_tool_call(tool_selection_text),
    }

    records: list[Phase1MeasurementRecord] = []
    for name in OPERATIONS:
        operation = operations[name]
        for _ in range(3):
            operation()  # Warm up imports and allocator pools before sampling.
        durations: list[float] = []
        for _ in range(samples):
            started = time.perf_counter_ns()
            for _ in range(TIMING_BATCH_SIZE):
                operation()
            durations.append(
                (time.perf_counter_ns() - started)
                / TIMING_BATCH_SIZE
                / 1_000_000.0
            )

        # Keep allocation accounting independent from timing.  Starting a
        # fresh trace per invocation excludes allocations made by the harness
        # itself and preserves an operation-local peak.
        peaks: list[int] = []
        for _ in range(samples):
            tracing_before = tracemalloc.is_tracing()
            if not tracing_before:
                tracemalloc.start()
            try:
                operation()
                _, peak = tracemalloc.get_traced_memory()
                peaks.append(peak)
            finally:
                if not tracing_before:
                    tracemalloc.stop()
        records.append(
            Phase1MeasurementRecord(
                operation=name,
                samples=len(durations),
                p50_ms=statistics.median(durations),
                p95_ms=statistics.quantiles(durations, n=20)[18],
                peak_bytes=max(peaks),
            )
        )
    return tuple(records)


def check_regression(
    records: Sequence[Phase1MeasurementRecord],
    fixture: Mapping[str, object],
) -> list[str]:
    """Return one bounded message per measurement that regressed past tolerance."""

    measurement = fixture["measurement"]
    baseline = measurement["baseline"]
    tolerance = float(measurement.get("regression_tolerance", REGRESSION_TOLERANCE))
    by_operation = {record.operation: record for record in records}
    failures: list[str] = []
    for name in OPERATIONS:
        record = by_operation.get(name)
        reference = baseline.get(name)
        if record is None or reference is None:
            failures.append(f"{name}: measurement or baseline missing")
            continue
        p50_baseline = float(reference["p50_ms"])
        p50_limit = max(p50_baseline * (1 + tolerance), p50_baseline + TIMING_JITTER_FLOOR_MS)
        if record.p50_ms > p50_limit:
            failures.append(f"{name}: p50 regressed {record.p50_ms:.3f}ms over {float(reference['p50_ms']):.3f}ms")
        p95_baseline = float(reference["p95_ms"])
        p95_limit = max(p95_baseline * (1 + tolerance), p95_baseline + TIMING_JITTER_FLOOR_MS)
        if record.p95_ms > p95_limit:
            failures.append(f"{name}: p95 regressed {record.p95_ms:.3f}ms over {float(reference['p95_ms']):.3f}ms")
        if record.peak_bytes > int(reference["peak_bytes"]) * (1 + tolerance):
            failures.append(f"{name}: peak regressed {record.peak_bytes} over {int(reference['peak_bytes'])} bytes")
    return failures


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--samples", type=int, default=MIN_SAMPLES)
    args = parser.parse_args(argv)
    root = args.root.resolve()
    records = measure_runtime(root, args.samples)
    fixture = json.loads((root / FIXTURE_RELATIVE_PATH).read_text(encoding="utf-8"))
    regressions = check_regression(records, fixture)
    print(
        json.dumps(
            {
                "measurements": [record.to_payload() for record in records],
                "regressions": regressions,
            },
            sort_keys=True,
        )
    )
    return 1 if regressions else 0


if __name__ == "__main__":
    sys.exit(main())
