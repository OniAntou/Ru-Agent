from __future__ import annotations

import argparse
import asyncio
import json
import time
from pathlib import Path
from typing import Iterable

import httpx
from websockets.asyncio.client import connect


def percentile(samples: Iterable[float], percentile_value: float) -> float:
    values = sorted(float(sample) for sample in samples)
    if not values:
        raise ValueError("percentile requires at least one sample")
    if not 0 <= percentile_value <= 100:
        raise ValueError("percentile must be between 0 and 100")
    position = (len(values) - 1) * percentile_value / 100
    lower = int(position)
    upper = min(lower + 1, len(values) - 1)
    fraction = position - lower
    return values[lower] + (values[upper] - values[lower]) * fraction


def summarize_samples(samples: Iterable[float], *, failures: int) -> dict[str, float | int | None]:
    values = list(samples)
    return {
        "sample_count": len(values),
        "failures": failures,
        "p50_ms": round(percentile(values, 50), 3) if values else None,
        "p95_ms": round(percentile(values, 95), 3) if values else None,
    }


def _http_url(base_url: str, path: str) -> str:
    return base_url.rstrip("/") + path


def _websocket_url(base_url: str, path: str, token: str) -> str:
    scheme = "wss" if base_url.startswith("https://") else "ws"
    authority = base_url.split("://", 1)[1].rstrip("/")
    return f"{scheme}://{authority}{path}?token={token}"


def measure_http_settings(base_url: str, token: str, samples: int) -> dict[str, float | int | None]:
    timings: list[float] = []
    failures = 0
    headers = {"Authorization": f"Bearer {token}"}
    with httpx.Client(timeout=10.0, headers=headers) as client:
        client.get(_http_url(base_url, "/settings"))
        for _ in range(samples):
            started = time.perf_counter()
            try:
                response = client.get(_http_url(base_url, "/settings"))
                response.close()
                if response.status_code >= 500:
                    failures += 1
                else:
                    timings.append((time.perf_counter() - started) * 1000)
            except Exception:
                failures += 1
    return summarize_samples(timings, failures=failures)


async def _measure_websocket_once(uri: str) -> float:
    started = time.perf_counter()
    async with connect(uri, proxy=None, open_timeout=10, close_timeout=2):
        pass
    return (time.perf_counter() - started) * 1000


def measure_websockets(base_url: str, token: str, samples: int) -> dict[str, dict[str, float | int | None]]:
    result: dict[str, dict[str, float | int | None]] = {}
    for path in ("/chat", "/voice/stream"):
        timings: list[float] = []
        failures = 0
        uri = _websocket_url(base_url, path, token)
        for _ in range(samples):
            try:
                timings.append(asyncio.run(_measure_websocket_once(uri)))
            except Exception:
                failures += 1
        result[path] = summarize_samples(timings, failures=failures)
    return result


def benchmark_target(base_url: str, token: str, samples: int) -> dict[str, object]:
    return {
        "http_settings": measure_http_settings(base_url, token, samples),
        "websockets": measure_websockets(base_url, token, samples),
    }


def compare_targets(direct: dict[str, object], edge: dict[str, object]) -> dict[str, float | None]:
    direct_http = direct["http_settings"]
    edge_http = edge["http_settings"]
    direct_p50 = direct_http["p50_ms"]
    edge_p50 = edge_http["p50_ms"]
    direct_p95 = direct_http["p95_ms"]
    edge_p95 = edge_http["p95_ms"]
    return {
        "http_settings_edge_minus_direct_p50_ms": round(edge_p50 - direct_p50, 3)
        if direct_p50 is not None and edge_p50 is not None
        else None,
        "http_settings_edge_minus_direct_p95_ms": round(edge_p95 - direct_p95, 3)
        if direct_p95 is not None and edge_p95 is not None
        else None,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Benchmark direct FastAPI versus the ASP.NET Core edge")
    parser.add_argument("--direct-url", required=True)
    parser.add_argument("--edge-url", required=True)
    parser.add_argument("--token", required=True)
    parser.add_argument("--samples", type=int, default=20)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--max-relative-p95-overhead", type=float, default=None)
    args = parser.parse_args()
    if args.samples < 1:
        parser.error("--samples must be at least 1")

    direct = benchmark_target(args.direct_url, args.token, args.samples)
    edge = benchmark_target(args.edge_url, args.token, args.samples)
    comparison = compare_targets(direct, edge)
    payload = {
        "samples": args.samples,
        "direct": direct,
        "edge": edge,
        "comparison": comparison,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")

    if args.max_relative_p95_overhead is not None:
        direct_p95 = direct["http_settings"]["p95_ms"]
        edge_p95 = edge["http_settings"]["p95_ms"]
        if direct_p95 and edge_p95 > direct_p95 * (1 + args.max_relative_p95_overhead):
            return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
