from __future__ import annotations

from collections import Counter

from mini_gpt.ru_v6_3_dialogues import (
    AUTHORED_ROWS,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TURN_BAND_COUNTS,
)


def test_v6_3_authored_shape_and_quotas() -> None:
    assert len(AUTHORED_ROWS) == 180
    assert Counter(row["language"] for row in AUTHORED_ROWS) == EXPECTED_LANGUAGE_COUNTS
    assert Counter(row["mode"] for row in AUTHORED_ROWS) == EXPECTED_MODE_COUNTS
    assert Counter(row["turn_band"] for row in AUTHORED_ROWS) == EXPECTED_TURN_BAND_COUNTS
    assert len({row["messages"][-1]["content"].casefold() for row in AUTHORED_ROWS}) == 180


def test_v6_3_rows_keep_history_and_personality_voice() -> None:
    assert all(any(message["role"] == "assistant" for message in row["messages"][1:-1]) for row in AUTHORED_ROWS)
    assert all(row["response"].endswith("Nya~") for row in AUTHORED_ROWS)
    assert all("anh" in row["response"].casefold() and "em" in row["response"].casefold() for row in AUTHORED_ROWS if row["language"] != "en")
    assert all("anh" not in row["response"].casefold() and "em" not in row["response"].casefold() for row in AUTHORED_ROWS if row["language"] == "en")
