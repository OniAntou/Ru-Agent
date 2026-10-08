from dataclasses import dataclass


@dataclass(frozen=True)
class Job:
    id: str
    depends_on: tuple[str, ...] = ()
    max_attempts: int = 1
