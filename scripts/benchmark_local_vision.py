from __future__ import annotations

import argparse
import json
import statistics
import subprocess
import sys
import time
from collections.abc import Callable
from pathlib import Path
from typing import Any
from urllib.error import URLError
from urllib.request import Request, urlopen


PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.local_vision_probe import (  # noqa: E402
    VisionProbeConfig,
    build_request,
    build_server_command,
    parse_response,
)


_SMOKE_JPEG_B64 = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCABAAEADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDhKKKK+xPlQooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigD//Z"


def _query_gpu_mib() -> int:
    try:
        completed = subprocess.run(
            [
                "nvidia-smi",
                "--query-compute-apps=used_memory",
                "--format=csv,noheader,nounits",
            ],
            capture_output=True,
            check=False,
            text=True,
            timeout=5,
        )
    except (OSError, subprocess.SubprocessError):
        return 0
    if completed.returncode != 0:
        return 0
    total = 0
    for line in completed.stdout.splitlines():
        try:
            total += int(line.strip())
        except ValueError:
            continue
    return total


def _get_json(url: str, *, timeout: float) -> dict[str, object]:
    with urlopen(Request(url, method="GET"), timeout=timeout) as response:
        value = json.load(response)
    if not isinstance(value, dict):
        raise ValueError("vision server returned an invalid health response")
    return value


def _post_json(url: str, payload: dict[str, object], *, timeout: float) -> dict[str, object]:
    request = Request(
        url,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urlopen(request, timeout=timeout) as response:
        value = json.load(response)
    if not isinstance(value, dict):
        raise ValueError("vision server returned an invalid completion response")
    return value


def _wait_for_health(child: Any, base_url: str, *, timeout_seconds: float = 45.0) -> None:
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if child.poll() is not None:
            raise RuntimeError("local vision server exited before becoming healthy")
        try:
            _get_json(f"{base_url}/health", timeout=1.0)
            return
        except (OSError, URLError, ValueError, json.JSONDecodeError):
            time.sleep(0.1)
    raise TimeoutError("local vision server did not become healthy within 45 seconds")


def _stop_child(child: Any) -> None:
    if child.poll() is not None:
        child.wait(timeout=0)
        return
    child.terminate()
    try:
        child.wait(timeout=5)
    except subprocess.TimeoutExpired:
        child.kill()
        child.wait(timeout=5)


def _p95(values: list[float]) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = max(0, (95 * len(ordered) + 99) // 100 - 1)
    return ordered[index]


def run_benchmark(
    config: VisionProbeConfig,
    *,
    runs: int = 5,
    process_factory: Callable[..., Any] = subprocess.Popen,
    gpu_probe: Callable[[], int] = _query_gpu_mib,
) -> dict[str, object]:
    if not 1 <= runs <= 20:
        raise ValueError("runs must be between one and twenty")
    command = build_server_command(config)
    gpu_before = gpu_probe()
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    load_started = time.perf_counter()
    child = process_factory(
        command,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        shell=False,
        creationflags=creationflags,
    )
    runs_ms: list[float] = []
    gpu_peak = gpu_before
    base_url = f"http://127.0.0.1:{config.port}"
    try:
        _wait_for_health(child, base_url)
        load_ms = (time.perf_counter() - load_started) * 1000
        last_observation: dict[str, object] | None = None
        request = build_request((_SMOKE_JPEG_B64,))
        for _ in range(runs):
            started = time.perf_counter()
            last_observation = parse_response(
                _post_json(f"{base_url}/v1/chat/completions", request, timeout=15.0)
            )
            runs_ms.append((time.perf_counter() - started) * 1000)
            gpu_peak = max(gpu_peak, gpu_probe())
        print(json.dumps(last_observation, ensure_ascii=False))
        return {
            "load_ms": round(load_ms, 3),
            "runs_ms": [round(value, 3) for value in runs_ms],
            "p50_ms": round(statistics.median(runs_ms), 3),
            "p95_ms": round(_p95(runs_ms), 3),
            "max_ms": round(max(runs_ms), 3),
            "gpu_before_mib": gpu_before,
            "gpu_peak_mib": gpu_peak,
        }
    finally:
        _stop_child(child)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Run an in-memory local Qwen3-VL smoke benchmark.")
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--port", type=int, default=18111)
    args = parser.parse_args(argv)
    result = run_benchmark(VisionProbeConfig(PROJECT_ROOT, args.port), runs=args.runs)
    print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
