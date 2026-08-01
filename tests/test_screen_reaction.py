from __future__ import annotations

import pytest

from mini_gpt.screen_reaction import (
    ReactionCoordinator,
    ReactionRequest,
    VisionObservation,
    format_observation_prompt,
)


def observation(
    identifier: str = "obs-1",
    *,
    age_ms: int = 100,
    event: str = "anh vừa hụt ba phát",
    novelty: float = 0.9,
    worthiness: float = 0.9,
) -> ReactionRequest:
    return ReactionRequest(
        observation_id=identifier,
        event_age_ms=age_ms,
        observation=VisionObservation(
            scene="game bắn súng",
            event=event,
            confidence=0.9,
            novelty=novelty,
            comment_worthiness=worthiness,
            visible_text=("PRIVATE MESSAGE: mật khẩu là abc",),
        ),
    )


def test_policy_allows_distinct_high_value_event_and_preserves_visible_text() -> None:
    coordinator = ReactionCoordinator(clock=lambda: 100.0)

    decision = coordinator.begin(observation())

    assert decision.allowed is True
    assert decision.reason == "allowed"
    assert "PRIVATE MESSAGE: mật khẩu là abc" in format_observation_prompt(
        observation().observation, ()
    )


def test_policy_rejects_stale_repeated_and_too_frequent_events() -> None:
    now = {"value": 100.0}
    coordinator = ReactionCoordinator(clock=lambda: now["value"])
    assert coordinator.begin(observation("first")).allowed
    coordinator.complete("first", "Câu đầu")

    assert coordinator.begin(observation("stale", age_ms=4501)).reason == "stale"
    now["value"] = 101.0
    assert coordinator.begin(observation("repeat")).reason == "repeat"
    assert coordinator.begin(observation("distinct", event="anh vừa thua trận")).reason == "cooldown"


def test_routine_event_waits_twenty_seconds_after_completed_reaction() -> None:
    now = {"value": 100.0}
    coordinator = ReactionCoordinator(clock=lambda: now["value"])
    assert coordinator.begin(observation("first")).allowed
    coordinator.complete("first", "Câu đầu")

    now["value"] = 115.0
    decision = coordinator.begin(
        observation("routine", event="màn hình chuyển cảnh", novelty=0.6, worthiness=0.65)
    )
    assert decision.reason == "routine_cooldown"
    now["value"] = 120.0
    assert coordinator.begin(
        observation("routine-ready", event="video bắt đầu", novelty=0.6, worthiness=0.65)
    ).allowed


def test_policy_rejects_low_confidence_but_accepts_low_subjective_scores() -> None:
    coordinator = ReactionCoordinator(clock=lambda: 100.0)
    low_confidence = observation().observation
    request = ReactionRequest(
        observation_id="low-confidence",
        event_age_ms=100,
        observation=VisionObservation(
            scene=low_confidence.scene,
            event=low_confidence.event,
            confidence=0.5,
            novelty=0.9,
            comment_worthiness=0.9,
        ),
    )
    assert coordinator.begin(request).reason == "low_confidence"
    assert coordinator.begin(observation("low-value", novelty=0.5, worthiness=0.59)).allowed


def test_policy_allows_confident_event_when_subjective_local_scores_are_zero() -> None:
    coordinator = ReactionCoordinator(clock=lambda: 100.0)

    decision = coordinator.begin(observation("zero-opinion", novelty=0.0, worthiness=0.0))

    assert decision.allowed is True
    assert decision.reason == "allowed"


def test_cancel_marks_only_current_pending_generation() -> None:
    coordinator = ReactionCoordinator(clock=lambda: 100.0)
    assert coordinator.begin(observation("current")).allowed

    coordinator.cancel("current")

    assert coordinator.is_cancelled("current") is True
    assert coordinator.is_cancelled("different") is False
    coordinator.complete("current", "Không được lưu")
    assert coordinator.recent_reactions() == ()


def test_pending_request_blocks_another_until_failure_releases_it() -> None:
    coordinator = ReactionCoordinator(clock=lambda: 100.0)
    assert coordinator.begin(observation("current")).allowed
    assert coordinator.begin(observation("other", event="một sự kiện khác")).reason == "busy"

    coordinator.fail("current")

    assert coordinator.begin(observation("other", event="một sự kiện khác")).allowed


def test_prompt_contains_exact_observation_and_only_six_recent_reactions() -> None:
    recent = tuple(f"reaction-{index}" for index in range(8))

    prompt = format_observation_prompt(observation().observation, recent)

    assert '"scene": "game bắn súng"' in prompt
    assert '"visible_text": ["PRIVATE MESSAGE: mật khẩu là abc"]' in prompt
    assert "reaction-0" not in prompt
    assert "reaction-1" not in prompt
    assert "reaction-2" in prompt
    assert "reaction-7" in prompt


def test_prompt_rejects_oversized_content_instead_of_truncating() -> None:
    oversized = VisionObservation(
        scene="scene",
        event="x" * 5900,
        confidence=0.9,
        novelty=0.9,
        comment_worthiness=0.9,
    )

    with pytest.raises(ValueError, match="6,000"):
        format_observation_prompt(oversized, ())
