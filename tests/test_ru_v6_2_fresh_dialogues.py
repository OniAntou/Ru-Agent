from __future__ import annotations

from collections import Counter

from mini_gpt.ru_v6_2_fresh_dialogues import (
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TURN_BAND_COUNTS,
    FRESH_AUTHORED_ROWS,
)


def test_authored_rows_have_expected_quotas_and_context() -> None:
    assert len(FRESH_AUTHORED_ROWS) == 1200
    assert Counter(row["language"] for row in FRESH_AUTHORED_ROWS) == EXPECTED_LANGUAGE_COUNTS
    assert Counter(row["mode"] for row in FRESH_AUTHORED_ROWS) == EXPECTED_MODE_COUNTS
    assert Counter(row["turn_band"] for row in FRESH_AUTHORED_ROWS) == EXPECTED_TURN_BAND_COUNTS
    assert all(any(message["role"] == "assistant" for message in row["messages"][1:-1]) for row in FRESH_AUTHORED_ROWS)


def test_authored_rows_follow_current_personality_by_language() -> None:
    for row in FRESH_AUTHORED_ROWS:
        language = row["language"]
        assert "Always append 'Nya~'" not in row["messages"][0]["content"]
        assert "Nya~" not in row["response"]
        if language == "en":
            assert "anh" not in row["response"].casefold()
        if language == "mixed":
            assert any(token in row["response"].casefold() for token in ("dramatic", "listening", "overthink", "okay"))
