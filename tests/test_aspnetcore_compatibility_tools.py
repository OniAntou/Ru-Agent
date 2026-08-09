from __future__ import annotations

from aspnetcore_compatibility_harness import classify_route_for_safe_probe, redact_secrets
from benchmark_aspnetcore_compat import percentile, summarize_samples


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


def test_safe_probe_skips_mutating_routes() -> None:
    assert classify_route_for_safe_probe(
        {"method": "POST", "transport": "http", "path": "/settings"}
    ) == "skip: mutating HTTP method"
    assert classify_route_for_safe_probe(
        {"method": "GET", "transport": "http", "path": "/status"}
    ) == "probe"
    assert classify_route_for_safe_probe(
        {"method": "WS", "transport": "websocket", "path": "/chat"}
    ) == "websocket"


def test_redaction_removes_secrets_without_retaining_body_text() -> None:
    text = redact_secrets(
        "Authorization: Bearer local-secret; response body: private text",
        ["local-secret", "private text"],
    )

    assert "local-secret" not in text
    assert "private text" not in text
    assert "[REDACTED]" in text
