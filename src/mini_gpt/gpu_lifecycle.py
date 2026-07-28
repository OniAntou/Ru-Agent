from __future__ import annotations

from collections.abc import Callable
from time import perf_counter


class GpuLifecycle:
    def __init__(
        self,
        lock,
        *,
        release_local_runtime: Callable[[], None],
        voice_server_available: Callable[[], bool],
        ensure_voice_server: Callable[[], None],
        request_voice_control: Callable[[str, dict[str, str] | None], object],
        clock: Callable[[], float] = perf_counter,
    ) -> None:
        self._lock = lock
        self._release_local_runtime = release_local_runtime
        self._voice_server_available = voice_server_available
        self._ensure_voice_server = ensure_voice_server
        self._request_voice_control = request_voice_control
        self._clock = clock

    def warm_voice_for_cloud(self, profile: str, set_status: Callable[[str], None]) -> float:
        with self._lock:
            self._release_local_runtime()
            set_status("Warming Gwen voice...")
            self._ensure_voice_server()
            started = self._clock()
            self._request_voice_control("/warmup", {"voice_profile": profile})
            return max(0.0, self._clock() - started)

    def prepare_local_generation(self) -> None:
        with self._lock:
            if self._voice_server_available():
                self._request_voice_control("/release", None)

    def prepare_speech(self) -> None:
        with self._lock:
            self._release_local_runtime()
            self._ensure_voice_server()
