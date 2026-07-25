import json
from pathlib import Path

import pytest

from mini_gpt.ru_v6_1_corpus import (
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_SPLIT_COUNTS,
    EXPECTED_TOTAL,
    build_ru_v6_1_corpus,
)


def _read_rows(path: Path):
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def test_v6_1_builder_writes_approved_shape_and_manifest(tmp_path):
    report = build_ru_v6_1_corpus(tmp_path)

    assert report["total_examples"] == EXPECTED_TOTAL == 2400
    assert report["split_counts"] == EXPECTED_SPLIT_COUNTS == {
        "train": 1680,
        "validation": 360,
        "held_out": 360,
    }
    assert report["language_counts"] == EXPECTED_LANGUAGE_COUNTS
    assert report["mode_counts"] == EXPECTED_MODE_COUNTS
    assert report["intensity_counts"] == {
        "neutral": 2040,
        "light": 288,
        "adult_boundary": 72,
    }
    assert report["turn_band_counts"] == {
        "short": 480,
        "medium": 1200,
        "long": 720,
    }
    assert report["prompt_group_count"] == 2400
    assert (tmp_path / "train.jsonl").is_file()
    assert (tmp_path / "val.jsonl").is_file()
    assert (tmp_path / "held_out.jsonl").is_file()
    assert json.loads((tmp_path / "manifest.json").read_text(encoding="utf-8")) == report


def test_v6_1_final_prompts_do_not_overlap_between_splits(tmp_path):
    build_ru_v6_1_corpus(tmp_path)
    split_prompts = {
        split: {row["messages"][-1]["content"] for row in _read_rows(tmp_path / filename)}
        for split, filename in (
            ("train", "train.jsonl"),
            ("validation", "val.jsonl"),
            ("held_out", "held_out.jsonl"),
        )
    }

    assert not split_prompts["train"] & split_prompts["validation"]
    assert not split_prompts["train"] & split_prompts["held_out"]
    assert not split_prompts["validation"] & split_prompts["held_out"]


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


def test_v6_1_rejects_invalid_source_row(tmp_path, monkeypatch):
    import mini_gpt.ru_v6_1_corpus as corpus

    original = corpus.V6_1_ROWS
    broken = [dict(row) for row in original]
    broken[0] = dict(broken[0])
    broken[0]["response"] = "Tôi là một mô hình ngôn ngữ AI."
    monkeypatch.setattr(corpus, "V6_1_ROWS", broken)

    with pytest.raises(ValueError, match="vietnamese_voice|ai_disclaimer"):
        corpus.build_ru_v6_1_corpus(tmp_path)
