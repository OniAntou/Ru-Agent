from collections import Counter

from mini_gpt.ru_v7_dialogues import (
    V7_LANGUAGE_COUNTS,
    V7_MODE_COUNTS,
    V7_TOTAL_ROWS,
    v7_dialogue_records,
)


def test_v7_source_has_the_approved_unique_shape():
    rows = v7_dialogue_records()

    assert len(rows) == V7_TOTAL_ROWS == 180
    assert Counter(row["language"] for row in rows) == Counter(V7_LANGUAGE_COUNTS)
    assert Counter(row["mode"] for row in rows) == Counter(V7_MODE_COUNTS)
    final_prompts = [row["messages"][-1]["content"] for row in rows]
    assert len(final_prompts) == len(set(final_prompts))


def test_v7_source_preserves_roles_and_project_provenance():
    for row in v7_dialogue_records():
        assert row["source"] == "teacher-curated-project-owned"
        assert row["license"] == "project-owned"
        assert row["messages"][0]["role"] == "system"
        assert row["messages"][1]["role"] == "user"
        assert row["messages"][-1]["role"] == "user"
        assert row["response"].strip()


def test_v7_source_limits_light_flirtation_to_eighteen_rows():
    rows = v7_dialogue_records()

    assert sum(row["flirt_level"] == "light" for row in rows) <= 18
    assert {row["flirt_level"] for row in rows} <= {"none", "light"}
