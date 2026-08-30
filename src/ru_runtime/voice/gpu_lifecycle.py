from __future__ import annotations

import logging
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from time import perf_counter

from .runtime import VoiceSelection

logger = logging.getLogger(__name__)

_ENGINE_DISPLAY_NAMES = {
    "omnivoice": "OmniVoice",
    "edge": "Edge",
    "system": "System",
    "piper": "Piper",
}


@dataclass(frozen=True, slots=True)
class VoiceServiceController:
    available: Callable[[], bool]
    ensure: Callable[[VoiceSelection], bool | None]
    request: Callable[[str, dict[str, str] | None], object]


class GpuLifecycle:
    def __init__(
        self,
        lock,
        *,
        voice_services: Mapping[str, VoiceServiceController],
        clock: Callable[[], float] = perf_counter,
    ) -> None:
        self._lock = lock
        self._voice_services = dict(voice_services)
        self._clock = clock

    def warm_voice_for_cloud(
        self, selection: VoiceSelection, set_status: Callable[[str], None]
    ) -> float:
        with self._lock:
            controller = self._selected_controller(selection)
            set_status(f"Warming {self._engine_name(selection.engine_id)} voice...")
            ready = controller.ensure(selection)
            started = self._clock()
            if ready is not True:
                self._request_warmup(controller, selection)
            self._release_other_voice_services(selection.engine_id)
            return max(0.0, self._clock() - started)

    def prepare_speech(self, selection: VoiceSelection) -> None:
        with self._lock:
            controller = self._selected_controller(selection)
            ready = controller.ensure(selection)
            if ready is not True:
                self._request_warmup(controller, selection)
            self._release_other_voice_services(selection.engine_id)

    def _selected_controller(self, selection: VoiceSelection) -> VoiceServiceController:
        try:
            return self._voice_services[selection.engine_id]
        except KeyError as error:
            raise ValueError(f"No voice service configured for {selection.engine_id!r}") from error

    def _release_other_voice_services(self, selected_engine_id: str) -> None:
        for engine_id, controller in self._voice_services.items():
            if engine_id != selected_engine_id and controller.available():
                self._release_voice_service(controller)

    def _release_voice_service(self, controller: VoiceServiceController) -> None:
        result = controller.request("/release", None)
        if isinstance(result, dict) and result.get("released") is False:
            # Proceed but warn: another voice service may still hold resources.
            logger.warning(
                "Voice service did not confirm /release; another voice engine may remain active"
            )

    def _request_warmup(self, controller: VoiceServiceController, selection: VoiceSelection) -> None:
        result = controller.request("/warmup", {"voice_profile": selection.profile_id})
        if isinstance(result, dict) and result.get("ready") is False:
            raise RuntimeError(f"Voice warmup failed for engine {selection.engine_id!r}")

    @staticmethod
    def _engine_name(engine_id: str) -> str:
        return _ENGINE_DISPLAY_NAMES.get(engine_id, engine_id)
