import json

import pytest

from mini_gpt.ru_authored_dialogues import authored_dialogue_records
from mini_gpt.ru_qwen_corpus import build_ru_qwen_corpus


def test_builds_authored_corpus_with_reproducible_project_provenance(tmp_path):
    report = build_ru_qwen_corpus(authored_dialogue_records(), tmp_path, seed=7)

    assert report["total_examples"] == 120
    assert report["contextual_examples"] >= 30
    assert report["source_counts"] == {"ru_project_seed": 120}
    assert report["licenses"] == {"project-owned": 120}
    assert len(report["source_sha256"]) == 64
    assert (tmp_path / "train.jsonl").is_file()
    assert (tmp_path / "val.jsonl").is_file()
    assert json.loads((tmp_path / "manifest.json").read_text(encoding="utf-8")) == report
    assert not list(tmp_path.glob("*.partial"))


def test_same_authored_input_and_seed_produce_the_same_split(tmp_path):
    records = authored_dialogue_records()

    build_ru_qwen_corpus(records, tmp_path / "one", seed=9)
    build_ru_qwen_corpus(records, tmp_path / "two", seed=9)

    assert (tmp_path / "one" / "train.jsonl").read_bytes() == (tmp_path / "two" / "train.jsonl").read_bytes()
    assert (tmp_path / "one" / "val.jsonl").read_bytes() == (tmp_path / "two" / "val.jsonl").read_bytes()


def test_refuses_to_publish_when_the_authored_count_is_incomplete(tmp_path):
    output_dir = tmp_path / "output"

    with pytest.raises(ValueError, match="exactly 120"):
        build_ru_qwen_corpus(authored_dialogue_records()[:-1], output_dir)

    assert not output_dir.exists()
