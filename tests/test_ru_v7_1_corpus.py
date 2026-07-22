from __future__ import annotations

import copy
import json

import pytest

import mini_gpt.ru_v7_1_corpus as corpus
from mini_gpt.ru_v7_1_corpus import build_ru_v7_1_corpus
from mini_gpt.ru_v7_1_dialogues import v7_1_repair_records
from mini_gpt.ru_v7_dialogues import v7_dialogue_records


def _rows() -> list[dict[str, object]]:
    return v7_1_repair_records()


@pytest.mark.parametrize(
    "bad_response",
    [
        "Tôi chỉ muốn nói về cảm xúc của tôi.",
        "Em đang chờ anh. *winks* Nya~",
        "Anh mệt thì em muốn làm tình thật mạnh với anh.",
    ],
)
def test_v7_1_builder_rejects_bad_repair_patterns(tmp_path, bad_response):
    rows = _rows()
    rows[0] = {**rows[0], "response": bad_response}

    with pytest.raises(ValueError, match="quality gate"):
        build_ru_v7_1_corpus(rows, tmp_path)


def test_v7_1_builder_rejects_duplicate_repair_prompts(tmp_path):
    rows = _rows()
    rows[1] = copy.deepcopy(rows[1])
    rows[1]["messages"][-1]["content"] = rows[0]["messages"][-1]["content"]

    with pytest.raises(ValueError, match="duplicate final prompts"):
        build_ru_v7_1_corpus(rows, tmp_path)


def test_v7_1_builder_rejects_exact_cross_source_prompt_response_duplicates(tmp_path):
    rows = _rows()
    base = next(row for row in v7_dialogue_records() if row["messages"][-1]["content"] == "I feel a little tired today.")
    rows[10] = {
        **copy.deepcopy(base),
        "mode": "answer_first",
        "flirt_level": "none",
    }

    with pytest.raises(ValueError, match="exact duplicate"):
        build_ru_v7_1_corpus(rows, tmp_path)


def test_v7_1_builder_writes_expected_combined_shape_and_hashes(tmp_path):
    report = build_ru_v7_1_corpus(_rows(), tmp_path, seed=42)

    assert report["total_examples"] == 300
    assert report["split_counts"] == {"train": 228, "validation": 36, "held_out": 36}
    assert report["language_counts"] == {"vi": 96, "en": 120, "mixed": 84}
    assert report["repair_examples"] == 120
    assert report["cross_source_prompt_overlap_count"] == 3
    assert report["base_source_sha256"]
    assert report["repair_source_sha256"]
    assert report["combined_source_sha256"]
    assert all((tmp_path / name).is_file() for name in ("train.jsonl", "val.jsonl", "held_out.jsonl", "manifest.json"))


def test_v7_1_builder_is_deterministic_for_seed_42(tmp_path):
    first_dir = tmp_path / "first"
    second_dir = tmp_path / "second"
    first = build_ru_v7_1_corpus(_rows(), first_dir, seed=42)
    second = build_ru_v7_1_corpus(_rows(), second_dir, seed=42)

    assert first == second
    for name in ("train.jsonl", "val.jsonl", "held_out.jsonl", "manifest.json"):
        assert (first_dir / name).read_bytes() == (second_dir / name).read_bytes()


def test_v7_1_builder_removes_partial_files_after_write_failure(tmp_path, monkeypatch):
    def fail_after_partial(path, rows):
        path.write_text(json.dumps(rows[0], ensure_ascii=False), encoding="utf-8")
        raise OSError("forced write failure")

    monkeypatch.setattr(corpus, "_write_jsonl", fail_after_partial)

    with pytest.raises(OSError, match="forced write failure"):
        build_ru_v7_1_corpus(_rows(), tmp_path)

    assert not list(tmp_path.glob("*.partial"))
