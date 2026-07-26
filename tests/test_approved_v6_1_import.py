import json
from pathlib import Path

import pytest

from mini_gpt.approved_v6_1_import import (
    SELECTED_IDS,
    merge_approved_into_v6_1,
    select_approved_rows,
)
from mini_gpt.ru_v6_1_corpus import build_ru_v6_1_corpus


def _approved_row(identifier: str, prompt: str, response: str) -> dict[str, str]:
    return {
        "id": identifier,
        "prompt": prompt,
        "response": response,
        "checkpoint": "test",
        "created_at_utc": "2026-07-27T00:00:00Z",
    }


def _write_jsonl(path: Path, rows: list[dict[str, str]]) -> None:
    path.write_text(
        "\n".join(json.dumps(row, ensure_ascii=False) for row in rows) + "\n",
        encoding="utf-8",
    )


def test_selected_rows_are_three_distinct_casefold_prompts():
    rows = [
        _approved_row("5012176f-d8cf-4fd7-bba0-f94bac083b78", "em phải chào lại chứ", "À! Chào anh."),
        _approved_row("f352af5f-e597-4a61-a6ba-00595049f5b2", "Chào em", "Xin chào anh!"),
        _approved_row("0e124abf-c87a-47e6-be7d-4c1b73c02c72", "Em tên là gì?", "Em tên là Ru!"),
        _approved_row("3ae35c35-3a8e-4fc7-8dc9-ee8a9ae9abe6", "Hả?", "Anh đang chơi với em!"),
        _approved_row("f18ec194-4353-42f5-8dcf-c7c5abcb1391", "Em tên là gì?", "Em tên là Ru. Ahah~"),
        _approved_row("6e7f8d38-3b8d-41e1-b77e-7410c8ab44c4", "chào em", "Chào anh!"),
        _approved_row("657e3a2a-9209-4029-8bc2-7cfea355d8cb", "chào em", "Chào anh!"),
    ]

    selected, rejected = select_approved_rows(rows)

    assert [row["approval_id"] for row in selected] == list(SELECTED_IDS)
    assert len({row["messages"][-1]["content"].casefold() for row in selected}) == 3
    assert {item["id"] for item in rejected} == {
        "f352af5f-e597-4a61-a6ba-00595049f5b2",
        "3ae35c35-3a8e-4fc7-8dc9-ee8a9ae9abe6",
        "f18ec194-4353-42f5-8dcf-c7c5abcb1391",
        "657e3a2a-9209-4029-8bc2-7cfea355d8cb",
    }


def test_merge_is_train_only_and_idempotent(tmp_path):
    dataset_dir = tmp_path / "dataset"
    build_ru_v6_1_corpus(dataset_dir)
    approved_path = tmp_path / "approved.jsonl"
    _write_jsonl(
        approved_path,
        [
            _approved_row(identifier, f"Prompt {index}", f"Phản hồi {index}")
            for index, identifier in enumerate(SELECTED_IDS)
        ],
    )
    val_before = (dataset_dir / "val.jsonl").read_bytes()
    held_out_before = (dataset_dir / "held_out.jsonl").read_bytes()

    report = merge_approved_into_v6_1(dataset_dir, approved_path)
    train_first = (dataset_dir / "train.jsonl").read_bytes()
    manifest_first = (dataset_dir / "manifest.json").read_bytes()
    second_report = merge_approved_into_v6_1(dataset_dir, approved_path)

    assert report["approved_import"]["accepted_count"] == 3
    assert report["split_counts"] == {"train": 1683, "validation": 360, "held_out": 360}
    assert second_report == report
    assert (dataset_dir / "train.jsonl").read_bytes() == train_first
    assert (dataset_dir / "manifest.json").read_bytes() == manifest_first
    assert (dataset_dir / "val.jsonl").read_bytes() == val_before
    assert (dataset_dir / "held_out.jsonl").read_bytes() == held_out_before


def test_merge_rejects_casefold_collision_before_writing(tmp_path):
    dataset_dir = tmp_path / "dataset"
    build_ru_v6_1_corpus(dataset_dir)
    train_path = dataset_dir / "train.jsonl"
    train_before = train_path.read_bytes()
    existing_prompt = json.loads(train_before.splitlines()[0])["messages"][-1]["content"]
    approved_path = tmp_path / "approved.jsonl"
    _write_jsonl(
        approved_path,
        [
            _approved_row(SELECTED_IDS[0], existing_prompt.swapcase(), "Phản hồi không được chèn"),
            _approved_row(SELECTED_IDS[1], "Prompt riêng hai", "Phản hồi hai"),
            _approved_row(SELECTED_IDS[2], "Prompt riêng ba", "Phản hồi ba"),
        ],
    )

    with pytest.raises(ValueError, match="prompt collision"):
        merge_approved_into_v6_1(dataset_dir, approved_path)

    assert train_path.read_bytes() == train_before
