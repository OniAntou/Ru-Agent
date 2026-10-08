import json

from .records import FrameResult


class JsonFrameDecoder:
    def __init__(self, max_frame_bytes: int = 65536) -> None:
        if isinstance(max_frame_bytes, bool) or not isinstance(max_frame_bytes, int):
            raise TypeError("max_frame_bytes must be an integer")
        if max_frame_bytes <= 0:
            raise ValueError("max_frame_bytes must be positive")
        self.max_frame_bytes = max_frame_bytes
        self._header = bytearray()
        self._payload = bytearray()
        self._expected: int | None = None
        self._skip_remaining = 0
        self._next_index = 0
        self._finished = False

    def feed(self, chunk: bytes) -> list[FrameResult]:
        if self._finished:
            raise RuntimeError("decoder is finished")
        if not isinstance(chunk, (bytes, bytearray, memoryview)):
            raise TypeError("chunk must be bytes-like")
        incoming = memoryview(chunk).cast("B")
        offset = 0
        results: list[FrameResult] = []
        while offset < len(incoming) or self._expected == 0:
            if self._skip_remaining:
                skipped = min(self._skip_remaining, len(incoming) - offset)
                self._skip_remaining -= skipped
                offset += skipped
                continue
            if self._expected is None:
                needed = 4 - len(self._header)
                taken = min(needed, len(incoming) - offset)
                self._header.extend(incoming[offset:offset + taken])
                offset += taken
                if len(self._header) < 4:
                    continue
                size = int.from_bytes(self._header, "big")
                self._header.clear()
                if size > self.max_frame_bytes:
                    results.append(self._result(None, "frame_too_large"))
                    self._skip_remaining = size
                    continue
                self._expected = size
            needed = self._expected - len(self._payload)
            taken = min(needed, len(incoming) - offset)
            self._payload.extend(incoming[offset:offset + taken])
            offset += taken
            if len(self._payload) == self._expected:
                results.append(self._decode_payload())
                self._payload.clear()
                self._expected = None
        return results

    def finish(self) -> list[FrameResult]:
        if self._finished:
            return []
        self._finished = True
        has_partial = bool(self._header or self._payload or self._expected is not None)
        self._header.clear()
        self._payload.clear()
        self._expected = None
        self._skip_remaining = 0
        return [self._result(None, "truncated_frame")] if has_partial else []

    def _decode_payload(self) -> FrameResult:
        try:
            text = self._payload.decode("utf-8", errors="strict")
        except UnicodeDecodeError:
            return self._result(None, "invalid_utf8")
        try:
            value = json.loads(text)
        except (json.JSONDecodeError, ValueError, RecursionError):
            return self._result(None, "invalid_json")
        return self._result(value, None)

    def _result(self, value: object | None, error: str | None) -> FrameResult:
        result = FrameResult(self._next_index, value, error)
        self._next_index += 1
        return result
