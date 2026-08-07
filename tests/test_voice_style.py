from __future__ import annotations

import pytest

from mini_gpt.voice_style import (
    ORIGINAL_STYLE_ID,
    voice_style_by_id,
    voice_style_from_saved,
)


def test_original_is_the_only_supported_voice_style() -> None:
    style = voice_style_by_id("original")

    assert style.id == ORIGINAL_STYLE_ID
    assert style.label
    assert not hasattr(style, "pitch_semitones")


@pytest.mark.parametrize("retired", ["low_pitch", "high_pitch", "whisper"])
def test_saved_retired_style_falls_back_to_original(retired: str) -> None:
    assert voice_style_from_saved(retired).id == ORIGINAL_STYLE_ID


def test_saved_missing_or_malformed_style_falls_back_to_original() -> None:
    assert voice_style_from_saved(None).id == ORIGINAL_STYLE_ID
    assert voice_style_from_saved("invalid").id == ORIGINAL_STYLE_ID
    assert voice_style_from_saved(["invalid"]).id == ORIGINAL_STYLE_ID


@pytest.mark.parametrize("retired", ["low_pitch", "high_pitch", "whisper"])
def test_direct_retired_style_is_rejected(retired: str) -> None:
    with pytest.raises(ValueError, match="Unsupported OmniVoice voice style"):
        voice_style_by_id(retired)


def test_direct_unknown_style_is_rejected() -> None:
    with pytest.raises(ValueError, match="Unsupported OmniVoice voice style"):
        voice_style_by_id("custom prose")
