from __future__ import annotations

from mini_gpt.routers.observation import (
    _MAX_OBSERVATION_EVENT_CHARS,
    _MAX_OBSERVATION_SCENE_CHARS,
    _MAX_OBSERVATION_VISIBLE_TEXT_CHARS,
    _MAX_OBSERVATION_VISIBLE_TEXT_ITEMS,
    _bounded_observation,
)
from mini_gpt.routers.observation import VisionObservationPayload


def test_maximum_valid_payload_never_exceeds_prompt_budget() -> None:
    payload = VisionObservationPayload(
        scene="x" * 500,
        event="y" * 2000,
        confidence=0.9,
        novelty=0.9,
        comment_worthiness=0.9,
        visible_text=["t" * 200] * 16,
    )

    bounded = _bounded_observation(payload)

    assert len(bounded.scene) <= _MAX_OBSERVATION_SCENE_CHARS
    assert len(bounded.event) <= _MAX_OBSERVATION_EVENT_CHARS
    assert len(bounded.visible_text) <= _MAX_OBSERVATION_VISIBLE_TEXT_ITEMS
    assert all(len(item) <= _MAX_OBSERVATION_VISIBLE_TEXT_CHARS for item in bounded.visible_text)


def test_format_observation_prompt_does_not_raise_on_maximum_payload() -> None:
    from mini_gpt.screen_reaction import format_observation_prompt

    payload = VisionObservationPayload(
        scene="x" * 500,
        event="y" * 2000,
        confidence=0.9,
        novelty=0.9,
        comment_worthiness=0.9,
        visible_text=["t" * 200] * 16,
    )
    bounded = _bounded_observation(payload)

    prompt = format_observation_prompt(bounded, ())

    assert len(prompt) <= 6_000
    assert "x" * (_MAX_OBSERVATION_SCENE_CHARS + 1) not in prompt