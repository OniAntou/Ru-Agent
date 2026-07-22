import copy

import pytest

from mini_gpt.ru_v7_corpus import build_ru_v7_corpus
from mini_gpt.ru_v7_dialogues import v7_dialogue_records


def _rows():
    return v7_dialogue_records()


@pytest.mark.parametrize(
    "bad_response",
    [
        "Tôi chỉ muốn nói về cảm xúc của tôi.",
        "Em đang chờ anh. *winks* Nya~",
        "Anh mệt thì em muốn làm tình thật mạnh với anh.",
    ],
)
def test_v7_builder_rejects_bad_response_patterns(tmp_path, bad_response):
    rows = _rows()
    rows[0] = {**rows[0], "response": bad_response}

    with pytest.raises(ValueError, match="quality gate"):
        build_ru_v7_corpus(rows, tmp_path)


def test_v7_builder_rejects_duplicate_final_prompts(tmp_path):
    rows = _rows()
    rows[1] = copy.deepcopy(rows[1])
    rows[1]["messages"][-1]["content"] = rows[0]["messages"][-1]["content"]

    with pytest.raises(ValueError, match="duplicate"):
        build_ru_v7_corpus(rows, tmp_path)


def test_v7_builder_writes_deterministic_splits_and_manifest(tmp_path):
    report = build_ru_v7_corpus(_rows(), tmp_path, seed=42)

    assert report["split_counts"] == {"train": 135, "validation": 22, "held_out": 23}
    assert report["language_counts"] == {"vi": 72, "en": 72, "mixed": 36}
    assert report["source_sha256"]
    assert (tmp_path / "train.jsonl").is_file()
    assert (tmp_path / "val.jsonl").is_file()
    assert (tmp_path / "held_out.jsonl").is_file()
    assert (tmp_path / "manifest.json").is_file()
