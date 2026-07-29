from __future__ import annotations

import threading

from mini_gpt.gpu_lifecycle import GpuLifecycle, VoiceServiceController
from mini_gpt.voice_runtime import VoiceSelection


def service(events: list[object], name: str, *, available: bool = True) -> VoiceServiceController:
    return VoiceServiceController(
        available=lambda: available,
        ensure=lambda: events.append(f"start {name}"),
        request=lambda path, payload=None: events.append((name, path, payload)),
    )


def test_omnivoice_warmup_releases_llama_and_other_voice_service() -> None:
    events: list[object] = []
    lifecycle = GpuLifecycle(
        threading.Lock(),
        release_local_runtime=lambda: events.append("release llama"),
        voice_services={
            "gwen": service(events, "gwen"),
            "omnivoice": service(events, "omnivoice", available=False),
        },
        clock=iter((10.0, 12.5)).__next__,
    )

    elapsed = lifecycle.warm_voice_for_cloud(
        VoiceSelection("Yến Nhi (OmniVoice — test)", "omnivoice", "yen_nhi"),
        lambda status: events.append(status),
    )

    assert elapsed == 2.5
    assert events == [
        "release llama",
        ("gwen", "/release", None),
        "Warming OmniVoice voice...",
        "start omnivoice",
        ("omnivoice", "/warmup", {"voice_profile": "yen_nhi"}),
    ]


def test_gwen_warmup_releases_omnivoice_and_keeps_gwen_regression() -> None:
    events: list[object] = []
    lifecycle = GpuLifecycle(
        threading.Lock(),
        release_local_runtime=lambda: events.append("release llama"),
        voice_services={
            "gwen": service(events, "gwen", available=False),
            "omnivoice": service(events, "omnivoice"),
        },
        clock=iter((10.0, 11.0)).__next__,
    )

    lifecycle.warm_voice_for_cloud(
        VoiceSelection("Ru (Gwen)", "gwen", "ru"),
        lambda status: events.append(status),
    )

    assert events == [
        "release llama",
        ("omnivoice", "/release", None),
        "Warming Gwen voice...",
        "start gwen",
        ("gwen", "/warmup", {"voice_profile": "ru"}),
    ]


def test_local_generation_releases_every_available_voice_service() -> None:
    events: list[object] = []
    lifecycle = GpuLifecycle(
        threading.Lock(),
        release_local_runtime=lambda: events.append("release llama"),
        voice_services={
            "gwen": service(events, "gwen"),
            "omnivoice": service(events, "omnivoice"),
        },
    )

    lifecycle.prepare_local_generation()

    assert events == [("gwen", "/release", None), ("omnivoice", "/release", None)]
