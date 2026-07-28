from __future__ import annotations

import threading

from mini_gpt.gpu_lifecycle import GpuLifecycle


def test_cloud_voice_warmup_releases_local_runtime_before_warming_gwen() -> None:
    events: list[object] = []
    lifecycle = GpuLifecycle(
        threading.Lock(),
        release_local_runtime=lambda: events.append("release"),
        voice_server_available=lambda: False,
        ensure_voice_server=lambda: events.append("start"),
        request_voice_control=lambda path, payload=None: events.append((path, payload)),
        clock=iter((10.0, 12.5)).__next__,
    )

    elapsed = lifecycle.warm_voice_for_cloud("yen_nhi", lambda status: events.append(status))

    assert elapsed == 2.5
    assert events == [
        "release",
        "Warming Gwen voice...",
        "start",
        ("/warmup", {"voice_profile": "yen_nhi"}),
    ]
