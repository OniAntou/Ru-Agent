from __future__ import annotations

import threading

from ru_runtime.voice.gpu_lifecycle import GpuLifecycle, VoiceServiceController
from ru_runtime.voice.runtime import VoiceSelection


def service(events: list[object], name: str, *, available: bool = True) -> VoiceServiceController:
    return VoiceServiceController(
        available=lambda: available,
        ensure=lambda: events.append(f"start {name}"),
        request=lambda path, payload=None: events.append((name, path, payload)),
    )


def test_omnivoice_warmup_warms_voice_before_releasing_other_voice_services() -> None:
    events: list[object] = []
    lifecycle = GpuLifecycle(
        threading.Lock(),
        voice_services={"omnivoice": service(events, "omnivoice", available=False)},
        clock=iter((10.0, 12.5)).__next__,
    )

    elapsed = lifecycle.warm_voice_for_cloud(
        VoiceSelection("Ái Vy (OmniVoice)", "omnivoice", "ai_vy"),
        lambda status: events.append(status),
    )

    assert elapsed == 2.5
    assert events == [
        "Warming OmniVoice voice...",
        "start omnivoice",
        ("omnivoice", "/warmup", {"voice_profile": "ai_vy"}),
    ]


def test_voice_warmup_failure_does_not_release_other_services() -> None:
    events: list[object] = []
    lifecycle = GpuLifecycle(
        threading.Lock(),
        voice_services={
            "omnivoice": VoiceServiceController(
                available=lambda: False,
                ensure=lambda: events.append("start omnivoice"),
                request=lambda path, payload=None: {"ready": False},
            )
        },
        clock=iter((10.0, 12.5)).__next__,
    )

    try:
        lifecycle.warm_voice_for_cloud(
            VoiceSelection("Ái Vy (OmniVoice)", "omnivoice", "ai_vy"),
            lambda status: events.append(status),
        )
    except RuntimeError:
        pass
    else:
        raise AssertionError("expected warmup failure to raise RuntimeError")
