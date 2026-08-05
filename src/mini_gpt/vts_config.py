from __future__ import annotations

from dataclasses import dataclass

# name -> (minimum, maximum, default)
FLOAT_FIELDS: dict[str, tuple[float, float, float]] = {
    "sway_amplitude_idle": (5.0, 30.0, 10.0),
    "sway_amplitude_speaking": (5.0, 40.0, 16.0),
    "head_scale": (0.0, 200.0, 100.0),
    "eye_scale": (0.0, 200.0, 100.0),
    "micro_action_min_delay": (1.0, 120.0, 8.0),
    "micro_action_max_delay": (1.0, 120.0, 22.0),
    "pose_min_interval": (10.0, 600.0, 90.0),
    "pose_max_interval": (10.0, 600.0, 130.0),
    "breath_amplitude": (0.0, 5.0, 1.5),
    "breath_rate": (0.05, 0.5, 0.15),
}


@dataclass(frozen=True)
class MotionConfig:
    sway_amplitude_idle: float = 10.0
    sway_amplitude_speaking: float = 16.0
    head_scale: float = 100.0
    eye_scale: float = 100.0
    micro_action_min_delay: float = 8.0
    micro_action_max_delay: float = 22.0
    pose_min_interval: float = 90.0
    pose_max_interval: float = 130.0
    breath_amplitude: float = 1.5
    breath_rate: float = 0.15

    @classmethod
    def defaults(cls) -> "MotionConfig":
        return cls()

    def to_dict(self) -> dict[str, float]:
        return {name: getattr(self, name) for name in FLOAT_FIELDS}

    @classmethod
    def from_dict(cls, data: dict) -> "MotionConfig":
        values: dict[str, float] = {}
        for name, (minimum, maximum, _default) in FLOAT_FIELDS.items():
            if name not in data:
                raise ValueError(f"missing motion config field: {name}")
            raw = data[name]
            try:
                value = float(raw)
            except (TypeError, ValueError):
                raise ValueError(f"{name} must be a number, got {raw!r}") from None
            if not minimum <= value <= maximum:
                raise ValueError(f"{name} must be between {minimum} and {maximum}, got {value}")
            values[name] = value
        if values["micro_action_max_delay"] < values["micro_action_min_delay"]:
            raise ValueError(
                f"micro_action_max_delay must be >= micro_action_min_delay "
                f"({values['micro_action_max_delay']} < {values['micro_action_min_delay']})"
            )
        if values["pose_max_interval"] < values["pose_min_interval"]:
            raise ValueError(
                f"pose_max_interval must be >= pose_min_interval "
                f"({values['pose_max_interval']} < {values['pose_min_interval']})"
            )
        return cls(**values)