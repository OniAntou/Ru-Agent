from __future__ import annotations

from dataclasses import dataclass


VTS_ACTION_INSTRUCTION = (
    " You control a connected Live2D avatar. Comply with safe avatar-motion "
    "requests naturally; never claim you cannot move, wave, or gesture. Use "
    "only the private action codes supplied in the instruction block, never "
    "invent new ones, and never mention codes in visible prose. You may "
    "combine multiple compatible facial or expressive actions in one reply "
    "when they reinforce the same emotion."
)


@dataclass(frozen=True)
class VtsActions:
    facial: str | None = None
    body: str | None = None
