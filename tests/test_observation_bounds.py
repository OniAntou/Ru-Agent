from __future__ import annotations

from ru_runtime.worker.v1.observation import (
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
    from ru_runtime.observation.screen_reaction import format_observation_prompt

    _, _, bounded = _parse_reaction(_maximum_payload())

    prompt = format_observation_prompt(bounded, ())

    assert len(prompt) <= 6_000
    assert "x" * (_MAX_OBSERVATION_SCENE_CHARS + 1) not in prompt


def test_observation_status_reports_real_loop_counters(monkeypatch) -> None:
    from ru_runtime.worker.v1 import observation
    from ru_runtime.worker.v1 import runtime_services as shared

    class FakeLoop:
        def status(self):
            return {
                "running": True,
                "interval_seconds": 1.0,
                "frame_count": 0,
                "vision_count": 0,
                "emitted_count": 0,
                "stale_drops": 2,
                "last_check_time": 0.0,
                "last_scene": None,
                "last_event": None,
                "last_reaction": None,
                "last_reaction_id": None,
                "last_reaction_at": None,
                "last_error": None,
            }

    monkeypatch.setattr(shared, "get_screen_observer_loop", lambda: FakeLoop())

    result = observation.observation_status()

    assert result["frame_count"] == 0
    assert result["vision_count"] == 0
    assert result["emitted_count"] == 0
    assert result["stale_drops"] == 2
