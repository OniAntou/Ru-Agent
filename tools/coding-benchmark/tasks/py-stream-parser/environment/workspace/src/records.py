from dataclasses import dataclass


@dataclass(frozen=True)
class FrameResult:
    index: int
    value: object | None
    error: str | None
