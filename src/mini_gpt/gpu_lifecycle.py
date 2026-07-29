from __future__ import annotations

from collections.abc import Callable, Mapping
from dataclasses import dataclass
from time import perf_counter

from .voice_runtime import VoiceSelection


@dataclass(frozen=True, slots=True)
class VoiceServiceController:
    available: Callable[[], bool]
    ensure: Callable[[], None]
    request: Callable[[str, dict[str, str] | None], object]


class GpuLifecycle:
    def __init__(
        self,
        lock,
        *,
        release_local_runtime: Callable[[], None],
        voice_services: Mapping[str, VoiceServiceController],
        clock: Callable[[], float] = perf_counter,
    ) -> None:
        self._lock = lock
        self._release_local_runtime = release_local_runtime
        self._voice_services = dict(voice_services)
        self._clock = clock

    def warm_voice_for_cloud(
        self, selection: VoiceSelection, set_status: Callable[[str], None]
    ) -> float:
        with self._lock:
            controller = self._selected_controller(selection)
            self._release_local_runtime()
            self._release_other_voice_services(selection.engine_id)
            set_status(f"Warming {self._engine_name(selection.engine_id)} voice...")
            controller.ensure()
            started = self._clock()
            controller.request("/warmup", {"voice_profile": selection.profile_id})
            return max(0.0, self._clock() - started)

    def prepare_local_generation(self) -> None:
        with self._lock:
            for controller in self._voice_services.values():
                if controller.available():
                    controller.request("/release", None)

    def prepare_speech(self, selection: VoiceSelection) -> None:
        with self._lock:
            controller = self._selected_controller(selection)
            self._release_local_runtime()
            self._release_other_voice_services(selection.engine_id)
            controller.ensure()
            controller.request("/warmup", {"voice_profile": selection.profile_id})

    def _selected_controller(self, selection: VoiceSelection) -> VoiceServiceController:
        try:
            return self._voice_services[selection.engine_id]
        except KeyError as error:
            raise ValueError(f"No voice service configured for {selection.engine_id!r}") from error

    def _release_other_voice_services(self, selected_engine_id: str) -> None:
        for engine_id, controller in self._voice_services.items():
            if engine_id != selected_engine_id and controller.available():
                controller.request("/release", None)

    @staticmethod
    def _engine_name(engine_id: str) -> str:
        return {"gwen": "Gwen", "omnivoice": "OmniVoice"}.get(engine_id, engine_id)
