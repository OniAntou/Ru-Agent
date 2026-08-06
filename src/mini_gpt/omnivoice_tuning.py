from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class OmniVoiceGenerationSettings:
    config_id: str
    language: str | None
    normalize_text: bool
    num_step: int

    def __post_init__(self) -> None:
        if not isinstance(self.config_id, str) or not self.config_id.strip():
            raise ValueError("config_id must be a non-empty string.")
        if self.language is not None and (
            not isinstance(self.language, str) or not self.language.strip()
        ):
            raise ValueError("language must be None or a non-empty string.")
        if not isinstance(self.normalize_text, bool):
            raise ValueError("normalize_text must be a boolean.")
        if isinstance(self.num_step, bool) or not isinstance(self.num_step, int) or self.num_step <= 0:
            raise ValueError("num_step must be a positive integer.")

    def generate_kwargs(self) -> dict[str, str | bool | int | None]:
        return {
            "language": self.language,
            "normalize_text": self.normalize_text,
            "num_step": self.num_step,
        }


BASELINE_SETTINGS = OmniVoiceGenerationSettings(
    config_id="baseline-16",
    language=None,
    normalize_text=False,
    num_step=16,
)
TEXT_AWARE_16_SETTINGS = OmniVoiceGenerationSettings(
    config_id="vi-normalized-16",
    language="Vietnamese",
    normalize_text=True,
    num_step=16,
)
TEXT_AWARE_20_SETTINGS = OmniVoiceGenerationSettings(
    config_id="vi-normalized-20",
    language="Vietnamese",
    normalize_text=True,
    num_step=20,
)
TEXT_AWARE_24_SETTINGS = OmniVoiceGenerationSettings(
    config_id="vi-normalized-24",
    language="Vietnamese",
    normalize_text=True,
    num_step=24,
)
CANDIDATE_SETTINGS = (
    BASELINE_SETTINGS,
    TEXT_AWARE_16_SETTINGS,
    TEXT_AWARE_20_SETTINGS,
    TEXT_AWARE_24_SETTINGS,
)

SETTINGS_BY_ID = {settings.config_id: settings for settings in CANDIDATE_SETTINGS}
_PROFILE_IDS = {"ru", "ai_vy"}

# This alters the active OmniVoice GUI runtime; its selection remains reversible.
GUI_TRIAL_SETTINGS = TEXT_AWARE_16_SETTINGS


def get_gui_trial_settings() -> OmniVoiceGenerationSettings:
    """Return the current GUI trial settings, read at call time."""
    return GUI_TRIAL_SETTINGS


def settings_for_profile(profile_id: str, config_id: str | None = None) -> OmniVoiceGenerationSettings:
    if profile_id not in _PROFILE_IDS:
        raise ValueError(f"Unsupported OmniVoice voice profile: {profile_id!r}")
    if config_id is None:
        return get_gui_trial_settings()
    try:
        return SETTINGS_BY_ID[config_id]
    except KeyError as error:
        raise ValueError(f"Unsupported OmniVoice configuration: {config_id!r}") from error
