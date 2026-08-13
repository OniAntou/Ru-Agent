from __future__ import annotations

from dataclasses import dataclass, field


@dataclass(slots=True)
class VoiceTiming:
    request_id: str
    voice_profile: str
    config_id: str
    values: dict[str, float] = field(default_factory=dict)

    def record(self, name: str, value_ms: float) -> None:
        if not name or not isinstance(value_ms, (int, float)) or value_ms < 0:
            raise ValueError("Voice timing values must be named, numeric, and non-negative.")
        self.values[name] = round(float(value_ms), 3)

    def to_dict(self) -> dict[str, object]:
        return {
            "request_id": self.request_id,
            "voice_profile": self.voice_profile,
            "config_id": self.config_id,
            "timings_ms": dict(self.values),
        }
