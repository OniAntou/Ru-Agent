from dataclasses import dataclass


@dataclass(frozen=True)
class Step:
    id: str
    dependencies: tuple[str, ...] = ()
