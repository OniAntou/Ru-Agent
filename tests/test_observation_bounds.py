from __future__ import annotations

from mini_gpt.runtime_observation import (
    _MAX_OBSERVATION_EVENT_CHARS,
    _MAX_OBSERVATION_SCENE_CHARS,
    _MAX_OBSERVATION_VISIBLE_TEXT_CHARS,
    _MAX_OBSERVATION_VISIBLE_TEXT_ITEMS,
    _parse_reaction,
)


def _maximum_payload() -> dict[str, object]:
    return {
        "observation_id": "obs-max",
        "event_age_ms": 0,
        "observation": {
            "scene": "x" * 500,
            "event": "y" * 2_000,
            "confidence": 0.9,
            "novelty": 0.9,
            "comment_worthiness": 0.9,
            "visible_text": ["t" * 200] * 16,
        },
    }


def test_maximum_valid_payload_never_exceeds_prompt_budget() -> None:
    _, _, bounded = _parse_reaction(_maximum_payload())

    assert len(bounded.scene) <= _MAX_OBSERVATION_SCENE_CHARS
    assert len(bounded.event) <= _MAX_OBSERVATION_EVENT_CHARS
    assert len(bounded.visible_text) <= _MAX_OBSERVATION_VISIBLE_TEXT_ITEMS
    assert all(len(item) <= _MAX_OBSERVATION_VISIBLE_TEXT_CHARS for item in bounded.visible_text)


def test_format_observation_prompt_does_not_raise_on_maximum_payload() -> None:
    from mini_gpt.screen_reaction import format_observation_prompt

    _, _, bounded = _parse_reaction(_maximum_payload())

    prompt = format_observation_prompt(bounded, ())

    assert len(prompt) <= 6_000
    assert "x" * (_MAX_OBSERVATION_SCENE_CHARS + 1) not in prompt
