import json

from .records import FrameResult


class JsonFrameDecoder:
    def __init__(self, max_frame_bytes: int = 65536) -> None:
        self.max_frame_bytes = max_frame_bytes
        self._buffer = bytearray()
        self._next_index = 0
        self._finished = False

    def feed(self, chunk: bytes) -> list[FrameResult]:
        if self._finished:
            raise RuntimeError("decoder is finished")
        self._buffer.extend(chunk)
        results: list[FrameResult] = []
        while len(self._buffer) >= 4:
            size = int.from_bytes(self._buffer[:4], "big")
            if size > self.max_frame_bytes:
                results.append(FrameResult(self._next_index, None, "frame_too_large"))
                self._next_index += 1
                del self._buffer[:4]
                self._buffer.clear()
                break
            if len(self._buffer) < size + 4:
                break
            payload = bytes(self._buffer[4:size + 4])
            del self._buffer[:size + 4]
            try:
                value = json.loads(payload.decode("utf-8"))
            except Exception:
                value = None
            results.append(FrameResult(self._next_index, value, None))
            self._next_index += 1
        return results

    def finish(self) -> list[FrameResult]:
        self._finished = True
        if not self._buffer:
            return []
        result = FrameResult(self._next_index, None, "truncated_frame")
        self._next_index += 1
        self._buffer.clear()
        return [result]
