from __future__ import annotations

import pytest

from mini_gpt.omnivoice_tuning import (
    BASELINE_SETTINGS,
    CANDIDATE_SETTINGS,
    GUI_TRIAL_SETTINGS,
    TEXT_AWARE_16_SETTINGS,
    TEXT_AWARE_20_SETTINGS,
    TEXT_AWARE_24_SETTINGS,
    OmniVoiceGenerationSettings,
)


def test_settings_expose_all_generate_arguments_including_none_language() -> None:
    settings = OmniVoiceGenerationSettings(
        config_id="baseline-16",
        language=None,
        normalize_text=False,
        num_step=16,
    )

    assert settings.generate_kwargs() == {
        "language": None,
        "normalize_text": False,
        "num_step": 16,
    }


@pytest.mark.parametrize(
    ("kwargs", "message"),
    [
        ({"config_id": " ", "language": None, "normalize_text": False, "num_step": 16}, "config_id"),
        ({"config_id": "valid", "language": " ", "normalize_text": False, "num_step": 16}, "language"),
        ({"config_id": "valid", "language": None, "normalize_text": 1, "num_step": 16}, "normalize_text"),
        ({"config_id": "valid", "language": None, "normalize_text": False, "num_step": 0}, "num_step"),
        ({"config_id": "valid", "language": None, "normalize_text": False, "num_step": True}, "num_step"),
    ],
)
def test_settings_reject_invalid_configuration_values(kwargs: dict[str, object], message: str) -> None:
    with pytest.raises(ValueError, match=message):
        OmniVoiceGenerationSettings(**kwargs)  # type: ignore[arg-type]


def test_frozen_settings_define_the_entire_candidate_matrix_in_order() -> None:
    assert BASELINE_SETTINGS == OmniVoiceGenerationSettings(
        config_id="baseline-16",
        language=None,
        normalize_text=False,
        num_step=16,
    )
    assert TEXT_AWARE_16_SETTINGS == OmniVoiceGenerationSettings(
        config_id="vi-normalized-16",
        language="Vietnamese",
        normalize_text=True,
        num_step=16,
    )
    assert TEXT_AWARE_20_SETTINGS == OmniVoiceGenerationSettings(
        config_id="vi-normalized-20",
        language="Vietnamese",
        normalize_text=True,
        num_step=20,
    )
    assert TEXT_AWARE_24_SETTINGS == OmniVoiceGenerationSettings(
        config_id="vi-normalized-24",
        language="Vietnamese",
        normalize_text=True,
        num_step=24,
    )
    assert CANDIDATE_SETTINGS == (
        BASELINE_SETTINGS,
        TEXT_AWARE_16_SETTINGS,
        TEXT_AWARE_20_SETTINGS,
        TEXT_AWARE_24_SETTINGS,
    )
    assert GUI_TRIAL_SETTINGS is TEXT_AWARE_24_SETTINGS
