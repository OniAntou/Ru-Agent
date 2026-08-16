from __future__ import annotations

import asyncio
import logging
import threading
from collections.abc import AsyncIterator, Callable
from dataclasses import dataclass
from time import perf_counter
from typing import Literal

from .chunks import split_tts_chunks
from .pacing import parse_voice_pace
from .observability import VoiceTiming
from .runtime import VoiceSelection, voice_selection_by_label
from .style import ORIGINAL_STYLE_ID, VoiceStyleId

VoiceStreamEventType = Literal["started", "audio", "ended", "cancelled", "failed"]
VoicePlaybackState = Literal["started", "ended", "cancelled", "failed"]

logger = logging.getLogger(__name__)


@dataclass(frozen=True, slots=True)
class VoiceStreamEvent:
    event_type: VoiceStreamEventType
    request_id: str
    sequence: int
    audio: bytes | None = None
    sample_rate: int | None = None
    audio_format: str | None = None
    error_code: str | None = None

    def control_payload(self) -> dict[str, object]:
        payload: dict[str, object] = {
            "type": self.event_type,
            "request_id": self.request_id,
            "sequence": self.sequence,
        }
        if self.sample_rate is not None:
            payload["sample_rate"] = self.sample_rate
        if self.audio_format is not None:
            payload["format"] = self.audio_format
        if self.error_code is not None:
            payload["error_code"] = self.error_code
        return payload


class VoiceStreamCoordinator:
    def __init__(
        self,
        *,
        prepare_speech: Callable[[VoiceSelection], None],
        request_audio: Callable[[str, str, float, VoiceStyleId], bytes],
        memory_lock: threading.Lock | None,
        lock_timeout: float = 120.0,
        request_timeout: float = 90.0,
    ) -> None:
        self._prepare_speech = prepare_speech
        self._request_audio = request_audio
        self._memory_lock = memory_lock
        self._lock_timeout = lock_timeout
        self._request_timeout = request_timeout
        self._cancelled: set[str] = set()
        self._cancel_lock = threading.Lock()
        self._timings: dict[str, VoiceTiming] = {}
        self._timing_started: dict[str, float] = {}
        self._current_request_id: str | None = None

    def cancel(self, request_id: str) -> None:
        with self._cancel_lock:
            self._cancelled.add(request_id)

    def _is_cancelled(self, request_id: str) -> bool:
        with self._cancel_lock:
            return request_id in self._cancelled

    def _finish_request(self, request_id: str) -> None:
        with self._cancel_lock:
            self._cancelled.discard(request_id)

    @staticmethod
    def _consume_background_worker_result(worker: asyncio.Future[object]) -> None:
        """Observe a worker result that outlived its stream timeout.

        ``asyncio.to_thread`` cannot stop a running thread. Reading its result
        from the completion callback prevents a late worker exception from
        being reported as an unobserved task error.
        """
        try:
            worker.result()
        except asyncio.CancelledError:
            return
        except Exception as error:
            logger.debug("Voice worker finished after stream cleanup: %s", error)

    @staticmethod
    def _complete_worker(
        completion: asyncio.Future[object],
        result: object | None,
        error: BaseException | None,
    ) -> None:
        if completion.done():
            return
        if error is not None:
            completion.set_exception(error)
        else:
            completion.set_result(result)

    def _start_worker(
        self,
        function: Callable[..., object],
        *args: object,
    ) -> tuple[asyncio.Task[object], asyncio.Future[object]]:
        """Start a thread worker and separately track its real completion.

        Cancelling the asyncio wrapper around ``to_thread`` does not terminate
        the operating-system thread. The completion future is fulfilled by the
        thread itself, so resource cleanup can wait for real completion.
        """
        loop = asyncio.get_running_loop()
        completion: asyncio.Future[object] = loop.create_future()

        def run() -> object:
            try:
                result = function(*args)
            except BaseException as error:
                try:
                    loop.call_soon_threadsafe(
                        self._complete_worker, completion, None, error
                    )
                except RuntimeError:
                    pass
                raise
            try:
                loop.call_soon_threadsafe(
                    self._complete_worker, completion, result, None
                )
            except RuntimeError:
                pass
            return result

        task = asyncio.create_task(asyncio.to_thread(run))
        task.add_done_callback(self._consume_background_worker_result)
        return task, completion

    def _defer_request_cleanup(
        self,
        worker_completion: asyncio.Future[object],
        request_id: str,
        release_memory_lock: bool,
    ) -> None:
        """Release request resources only after an uninterruptible worker ends."""

        def cleanup(completed_worker: asyncio.Task[object]) -> None:
            self._consume_background_worker_result(completed_worker)
            if release_memory_lock and self._memory_lock is not None:
                self._memory_lock.release()
            self._finish_request(request_id)

        worker_completion.add_done_callback(cleanup)

    def acknowledge_playback(self, request_id: str, state: str) -> bool:
        """Record one playback state for the latest request generation only."""
        if state not in {"started", "ended", "cancelled", "failed"}:
            return False
        with self._cancel_lock:
            if request_id != self._current_request_id:
                return False
            timing = self._timings.get(request_id)
            started_at = self._timing_started.get(request_id)
            if timing is None or started_at is None:
                return False
            if state == "started" and "playback_started_ms" in timing.values:
                return False
            if state != "started" and "playback_terminal_ms" in timing.values:
                return False
            elapsed_ms = max(0.0, (perf_counter() - started_at) * 1000.0)
            timing.record(f"playback_{state}_ms", elapsed_ms)
            if state != "started":
                timing.record("playback_terminal_ms", elapsed_ms)
            logger.debug("Voice playback acknowledgement: %s", timing.to_dict())
            return True

    def timing_snapshot(self, request_id: str) -> dict[str, object]:
        with self._cancel_lock:
            timing = self._timings.get(request_id)
            return timing.to_dict() if timing is not None else {}

    async def start(
        self,
        request_id: str,
        text: str,
        voice_label: str,
        speed: float,
        voice_style_id: VoiceStyleId = ORIGINAL_STYLE_ID,
    ) -> AsyncIterator[VoiceStreamEvent]:
        clean_text, _ = parse_voice_pace(text)
        chunks = split_tts_chunks(clean_text)
        if not request_id or not chunks:
            yield VoiceStreamEvent("failed", request_id, 0, error_code="invalid_request")
            return
        try:
            selection = voice_selection_by_label(voice_label)
        except ValueError:
            yield VoiceStreamEvent("failed", request_id, 0, error_code="unsupported_voice")
            return
        lock_acquired = self._memory_lock is None
        if self._memory_lock is not None:
            if not self._memory_lock.acquire(timeout=self._lock_timeout):
                yield VoiceStreamEvent("failed", request_id, 0, error_code="busy")
                return
            lock_acquired = True
        with self._cancel_lock:
            self._timings.clear()
            self._timing_started.clear()
            self._current_request_id = request_id
            self._timings[request_id] = VoiceTiming(request_id, selection.profile_id, "vi-normalized-16")
            self._timing_started[request_id] = perf_counter()
        worker_task: asyncio.Task[object] | None = None
        worker_completion: asyncio.Future[object] | None = None
        try:
            worker_task, worker_completion = self._start_worker(
                self._prepare_speech, selection
            )
            await asyncio.wait_for(
                asyncio.shield(worker_task),
                timeout=self._request_timeout,
            )
            worker_task = None
            worker_completion = None
            if self._is_cancelled(request_id):
                yield VoiceStreamEvent("cancelled", request_id, 0)
                return
            yield VoiceStreamEvent("started", request_id, 0)
            for sequence, chunk in enumerate(chunks):
                if self._is_cancelled(request_id):
                    yield VoiceStreamEvent("cancelled", request_id, sequence)
                    return
                worker_task, worker_completion = self._start_worker(
                    self._request_audio,
                    chunk,
                    voice_label,
                    speed,
                    voice_style_id,
                )
                audio = await asyncio.wait_for(
                    asyncio.shield(worker_task),
                    timeout=self._request_timeout,
                )
                worker_task = None
                worker_completion = None
                if self._is_cancelled(request_id):
                    yield VoiceStreamEvent("cancelled", request_id, sequence)
                    return
                yield VoiceStreamEvent(
                    "audio",
                    request_id,
                    sequence,
                    audio=audio,
                    sample_rate=24_000,
                    audio_format="wav",
                )
            yield VoiceStreamEvent("ended", request_id, len(chunks))
        except asyncio.TimeoutError:
            yield VoiceStreamEvent("failed", request_id, 0, error_code="synthesis_timeout")
        except Exception:
            yield VoiceStreamEvent("failed", request_id, 0, error_code="synthesis_failed")
        finally:
            if worker_completion is not None and not worker_completion.done():
                # A timed-out or externally cancelled await does not terminate
                # the to_thread operation. Holding the lock prevents a second
                # local model request from overlapping its GPU work.
                self._defer_request_cleanup(
                    worker_completion, request_id, lock_acquired
                )
            else:
                if worker_completion is not None:
                    self._consume_background_worker_result(worker_completion)
                if lock_acquired and self._memory_lock is not None:
                    self._memory_lock.release()
                self._finish_request(request_id)
