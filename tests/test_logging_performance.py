import time
from pathlib import Path

from ru_runtime.persistence.activity_outbox import ActivityOutboxWriter
from ru_runtime.persistence.diagnostic_log import DiagnosticLogWriter
from ru_runtime.persistence.diagnostic_schema import DiagnosticEvent


def test_diagnostic_enqueue_latency(tmp_path: Path):
    writer = DiagnosticLogWriter(log_dir=str(tmp_path), batch_size=100)
    try:
        latencies = []
        for i in range(1000):
            evt = DiagnosticEvent(
                event_id=f"evt_{i}",
                ts=time.time(),
                iso="2026-08-20T00:00:00.000Z",
                level="info",
                component="perf.test",
                event_code="app.start",
                duration_ms=1.5,
            )
            t0 = time.perf_counter()
            enqueued = writer.try_enqueue(evt)
            t1 = time.perf_counter()
            latencies.append((t1 - t0) * 1000.0)
            assert enqueued is True

        p95 = sorted(latencies)[int(len(latencies) * 0.95)]
        # Enqueue in memory queue should be fast (< 1.0 ms)
        assert p95 < 1.0, f"p95 enqueue latency was {p95:.3f} ms"
    finally:
        writer.close()


def test_activity_outbox_enqueue_latency(tmp_path: Path):
    writer = ActivityOutboxWriter(log_dir=str(tmp_path), capacity=500)
    try:
        latencies = []
        for _i in range(200):
            t0 = time.perf_counter()
            writer.try_enqueue({"type": "chat", "data": {"role": "user", "preview": "hello"}, "session_id": "s1"})
            t1 = time.perf_counter()
            latencies.append((t1 - t0) * 1000.0)

        p95 = sorted(latencies)[int(len(latencies) * 0.95)]
        assert p95 < 2.0, f"p95 outbox write latency was {p95:.3f} ms"
    finally:
        pass


def test_benchmark_uses_isolated_activity_writer_and_all_percentile_gates():
    benchmark = Path(__file__).resolve().parents[1] / "scripts" / "benchmark_logging.py"
    source = benchmark.read_text(encoding="utf-8")

    assert "activity_log.append" not in source
    assert "ActivityOutboxWriter(" in source
    assert "log_dir=str(activity_dir)" in source
    assert "Baseline p50" in source
    assert "Logging p99" in source
    assert "pct_p95" in source
    assert "pct_p99" in source
