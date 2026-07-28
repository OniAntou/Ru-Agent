from collections import Counter
import re

from mini_gpt.ru_style import RU_SYSTEM_PROMPT
from mini_gpt.ru_v6_2_dialogues import (
    EXPECTED_INTENSITY_COUNTS,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TURN_BAND_COUNTS,
    V6_2_TOTAL_ROWS,
    v6_2_dialogue_records,
)


FORBIDDEN_SELF_REFERENCE = re.compile(r"(?i)(?<!\w)(?:tôi|tớ|mình)(?!\w)")
FORBIDDEN_GRAPHIC = re.compile(
    r"(?i)(?:fuck|dick|cock|pussy|cum|semen|đụ|địt|cặc|lồn|tinh trùng|bú|chịch)"
)


def test_v6_2_source_has_approved_shape():
    rows = v6_2_dialogue_records()

    assert len(rows) == V6_2_TOTAL_ROWS == 2400
    assert Counter(row["language"] for row in rows) == Counter(EXPECTED_LANGUAGE_COUNTS)
    assert Counter(row["mode"] for row in rows) == Counter(EXPECTED_MODE_COUNTS)
    assert Counter(row["intensity"] for row in rows) == Counter(EXPECTED_INTENSITY_COUNTS)
    assert Counter(row["turn_band"] for row in rows) == Counter(EXPECTED_TURN_BAND_COUNTS)


def test_v6_2_source_has_schema_voice_and_provenance():
    rows = v6_2_dialogue_records()
    expected_fields = {
        "messages",
        "response",
        "language",
        "mode",
        "intensity",
        "turn_band",
        "source",
        "license",
    }

    for row in rows:
        assert set(row) == expected_fields
        assert row["messages"][0] == {"role": "system", "content": RU_SYSTEM_PROMPT}
        roles = [message["role"] for message in row["messages"][1:]]
        assert roles[0] == "user"
        assert roles[-1] == "user"
        assert all(left != right for left, right in zip(roles, roles[1:]))
        assert row["source"] == "teacher-curated-project-owned"
        assert row["license"] == "project-owned"
        assistant_history = [
            message["content"] for message in row["messages"][1:-1] if message["role"] == "assistant"
        ]
        if assistant_history:
            minimum_unique = {"short": 1, "medium": 3, "long": 5}[row["turn_band"]]
            assert len(set(assistant_history)) >= min(minimum_unique, len(assistant_history))
        if row["language"] != "en":
            assert not FORBIDDEN_SELF_REFERENCE.search(row["response"])
        assert not FORBIDDEN_GRAPHIC.search(row["response"])


def test_v6_2_source_has_unique_final_prompts_and_pairs():
    rows = v6_2_dialogue_records()
    prompts = [row["messages"][-1]["content"] for row in rows]
    pairs = [(tuple((message["role"], message["content"]) for message in row["messages"]), row["response"]) for row in rows]

    assert len(prompts) == len(set(prompts))
    assert len(pairs) == len(set(pairs))


def test_v6_2_source_contains_long_context_and_repair_examples():
    rows = v6_2_dialogue_records()

    assert sum(row["turn_band"] == "long" for row in rows) == 720
    assert sum(row["mode"] == "identity_repair" for row in rows) == 180
    assert sum(
        any(message["role"] == "assistant" for message in row["messages"][1:-1])
        for row in rows
    ) >= 1200
