from __future__ import annotations

import argparse
import asyncio
import json
import time
from collections.abc import Iterable
from pathlib import Path

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
    for path in ("/voice/stream",):
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


def main() -> int:
    parser = argparse.ArgumentParser(description="Benchmark the ASP.NET Core public backend")
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--token", required=True)
    parser.add_argument("--samples", type=int, default=20)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--max-http-p95-ms", type=float, default=None)
    args = parser.parse_args()
    if args.samples < 1:
        parser.error("--samples must be at least 1")

    payload = {
        "samples": args.samples,
        "aspnetcore": benchmark_target(args.base_url, args.token, args.samples),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")

    if args.max_http_p95_ms is not None:
        p95 = payload["aspnetcore"]["http_settings"]["p95_ms"]
        if p95 is None or p95 > args.max_http_p95_ms:
            return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
