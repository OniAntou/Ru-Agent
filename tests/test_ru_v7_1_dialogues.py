from __future__ import annotations

from collections import Counter
import re

from mini_gpt.ru_v7_1_dialogues import (
    LICENSE,
    REPAIR_LANGUAGE_COUNTS,
    REPAIR_MAX_LIGHT_FLIRT_ROWS,
    REPAIR_MODE_COUNTS,
    REPAIR_TOTAL_ROWS,
    SOURCE,
    v7_1_repair_records,
)


def test_v7_1_repair_source_has_exact_shape_and_provenance():
    rows = v7_1_repair_records()

    assert len(rows) == REPAIR_TOTAL_ROWS == 120
    assert Counter(row["language"] for row in rows) == Counter(REPAIR_LANGUAGE_COUNTS)
    assert Counter(row["mode"] for row in rows) == Counter(REPAIR_MODE_COUNTS)
    assert len({row["messages"][-1]["content"] for row in rows}) == len(rows)
    assert sum(row["flirt_level"] == "light" for row in rows) <= REPAIR_MAX_LIGHT_FLIRT_ROWS

    contextual = sum(
        any(message["role"] == "assistant" for message in row["messages"][1:-1])
        for row in rows
    )
    assert contextual >= 12

    for row in rows:
        assert row["source"] == SOURCE
        assert row["license"] == LICENSE
        assert row["messages"][0]["role"] == "system"
        assert row["messages"][-1]["role"] == "user"
        assert row["response"].strip()


def test_v7_1_repair_source_contains_all_known_v7_failure_prompts():
    prompts = {row["messages"][-1]["content"] for row in v7_1_repair_records()}

    assert {
        "I feel a little tired today.",
        "I just got home.",
        "I am having coffee right now.",
        "Ru, hôm nay I feel better.",
        "Today was hơi stressful.",
        "Anh đang having coffee với em.",
        "I am home now, but my head is still busy.",
    } <= prompts


def test_v7_1_mixed_rows_use_both_languages_and_keep_ru_voice():
    mixed_rows = [row for row in v7_1_repair_records() if row["language"] == "mixed"]

    for row in mixed_rows:
        response = str(row["response"]).casefold()
        assert any(token in response for token in ("anh", "em", "hôm", "nghe", "đó", "rồi"))
        assert re.search(
            r"\b(?:what|what's|which|would|how|do|does|is|are|the|a|to|with|you|slow|down|notifications|focus|message|schedule|problem|night)\b",
            response,
        )
        assert "tôi" not in response
