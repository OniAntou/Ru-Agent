from __future__ import annotations

from ru_runtime.voice.pacing import (
    DEFAULT_VOICE_SPEED,
    effective_voice_speed,
    parse_voice_pace,
    voice_pace_for,
    voice_speed_from_saved,
)


def test_voice_pace_marker_is_removed_before_visible_reply_or_tts() -> None:
    reply, pace = parse_voice_pace("Em giải thích chậm hơn nhé. [[VOICE_PACE:slow]]")

    assert reply == "Em giải thích chậm hơn nhé."
    assert pace == "slow"


def test_manual_speed_is_clamped_and_auto_pace_stays_near_manual_baseline() -> None:
    assert voice_speed_from_saved("invalid") == DEFAULT_VOICE_SPEED
    assert voice_speed_from_saved(9.0) == 1.2
    assert effective_voice_speed(0.9, auto_pace_enabled=False, pace="quick") == 0.9
    assert effective_voice_speed(0.9, auto_pace_enabled=True, pace="slow") == 0.82
    assert effective_voice_speed(1.17, auto_pace_enabled=True, pace="quick") == 1.2


def test_voice_pace_marker_in_the_middle_is_stripped() -> None:
    reply, pace = parse_voice_pace("Em [[VOICE_PACE:slow]] giải thích chậm nhé.")

    assert reply == "Em giải thích chậm nhé."
    assert pace == "slow"


def test_voice_pace_uses_the_last_of_several_markers() -> None:
    reply, pace = parse_voice_pace("[[VOICE_PACE:quick]] Chào anh. [[VOICE_PACE:slow]]")

    assert reply == "Chào anh."
    assert pace == "slow"


def test_voice_pace_without_a_marker_defaults_to_normal() -> None:
    reply, pace = parse_voice_pace("Chào anh hôm nay thế nào?")

    assert reply == "Chào anh hôm nay thế nào?"
    assert pace == "normal"

    # Heuristic inference from punctuation
    assert voice_pace_for("Anh vừa nói gì đó ã? Em hiểu rồi...") == "normal"
    assert voice_pace_for("Trời ơi! Mừng quá đi mất! Em vui lắm!") == "quick"
    assert voice_pace_for("Em hơi buồn... Anh nói thật lòng đi...") == "slow"
