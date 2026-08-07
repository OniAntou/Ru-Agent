from __future__ import annotations

import pytest

from mini_gpt.voice_style import (
    ORIGINAL_STYLE_ID,
    voice_style_by_id,
    voice_style_from_saved,
)


@pytest.mark.parametrize(
    ("style_id", "instruct"),
    [
        ("original", None),
        ("low_pitch", "low pitch"),
        ("high_pitch", "high pitch"),
        ("whisper", "whisper"),
    ],
)
def test_known_voice_styles_are_stable_and_explicit(
    style_id: str, instruct: str | None
) -> None:
    style = voice_style_by_id(style_id)

    assert style.id == style_id
    assert style.instruct == instruct


def test_saved_missing_or_malformed_style_falls_back_to_original() -> None:
    assert voice_style_from_saved(None).id == ORIGINAL_STYLE_ID
    assert voice_style_from_saved("invalid").id == ORIGINAL_STYLE_ID
    assert voice_style_from_saved(["invalid"]).id == ORIGINAL_STYLE_ID


def test_direct_unknown_style_is_rejected() -> None:
    with pytest.raises(ValueError, match="Unsupported OmniVoice voice style"):
        voice_style_by_id("custom prose")
