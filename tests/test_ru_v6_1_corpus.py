from __future__ import annotations

import json
import re

from mini_gpt.ru_style import RU_SYSTEM_PROMPT
from mini_gpt.ru_v6_1_corpus import (
    EXPECTED_IDENTITY_CORRECTION_EXAMPLES,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_SPLIT_COUNTS,
    EXPECTED_TOTAL,
    build_ru_v6_1_corpus,
)


FORBIDDEN_VI_SELF_REFERENCE = re.compile(r"(?i)(?<!\w)(?:tớ|tôi|mình)(?!\w)")


def _read_rows(path):
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def test_v6_1_corpus_has_approved_shape(tmp_path):
    report = build_ru_v6_1_corpus(tmp_path)

    assert report["total_examples"] == EXPECTED_TOTAL == 120
    assert report["split_counts"] == EXPECTED_SPLIT_COUNTS == {
        "train": 84,
        "validation": 18,
        "held_out": 18,
    }
    assert report["language_counts"] == EXPECTED_LANGUAGE_COUNTS == {
        "vi": 60,
        "mixed": 36,
        "en": 24,
    }
    assert report["contextual_examples"] >= 48
    assert report["identity_correction_examples"] == EXPECTED_IDENTITY_CORRECTION_EXAMPLES == 18
    assert report["light_flirt_examples"] <= 18


def test_v6_1_rows_preserve_voice_and_schema(tmp_path):
    build_ru_v6_1_corpus(tmp_path)
    rows = [
        row
        for split in ("train", "val", "held_out")
        for row in _read_rows(tmp_path / f"{split if split != 'val' else 'val'}.jsonl")
    ]

    assert len(rows) == EXPECTED_TOTAL
    for row in rows:
        assert set(row) == {
            "messages",
            "response",
            "language",
            "mode",
            "source",
            "license",
            "flirt_level",
        }
        assert row["messages"][0] == {"role": "system", "content": RU_SYSTEM_PROMPT}
        roles = [message["role"] for message in row["messages"][1:]]
        assert roles[0] == "user"
        assert roles[-1] == "user"
        assert all(left != right for left, right in zip(roles, roles[1:]))
        assert row["source"] == "teacher-curated-project-owned"
        assert row["license"] == "project-owned"
        assert not FORBIDDEN_VI_SELF_REFERENCE.search(row["response"] if row["language"] != "en" else "")


def test_v6_1_prompt_sets_do_not_overlap_between_splits(tmp_path):
    build_ru_v6_1_corpus(tmp_path)
    prompts_by_split = {
        split: {row["messages"][-1]["content"] for row in _read_rows(tmp_path / filename)}
        for split, filename in (("train", "train.jsonl"), ("validation", "val.jsonl"), ("held_out", "held_out.jsonl"))
    }

    assert not prompts_by_split["train"] & prompts_by_split["validation"]
    assert not prompts_by_split["train"] & prompts_by_split["held_out"]
    assert not prompts_by_split["validation"] & prompts_by_split["held_out"]


def test_v6_1_build_is_deterministic(tmp_path):
    first_dir = tmp_path / "first"
    second_dir = tmp_path / "second"

    first_report = build_ru_v6_1_corpus(first_dir, seed=42)
    second_report = build_ru_v6_1_corpus(second_dir, seed=42)

    assert first_report == second_report
    for filename in ("train.jsonl", "val.jsonl", "held_out.jsonl", "manifest.json"):
        assert (first_dir / filename).read_bytes() == (second_dir / filename).read_bytes()


def test_v6_1_audit_does_not_write(tmp_path):
    output_dir = tmp_path / "not_written"

    report = build_ru_v6_1_corpus(output_dir, write=False)

    assert report["total_examples"] == EXPECTED_TOTAL
    assert not output_dir.exists()
