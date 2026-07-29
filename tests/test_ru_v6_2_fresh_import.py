from __future__ import annotations

import json
from hashlib import sha256
from pathlib import Path

import pytest

from mini_gpt.ru_v6_2_fresh_import import (
    append_imports_to_train,
    load_approved_rows,
    load_memory_rows,
    select_import_rows,
)


def _write_jsonl(path: Path, rows: list[dict[str, object]]) -> None:
    path.write_text("\n".join(json.dumps(row, ensure_ascii=False) for row in rows) + "\n", encoding="utf-8")


def _mojibake(text: str) -> str:
    return text.encode("utf-8").decode("latin1")


def _base_row(prompt: str = "train prompt", response: str = "Em biết rồi.") -> dict[str, object]:
    return {
        "messages": [{"role": "system", "content": "system"}, {"role": "user", "content": prompt}],
        "response": response,
        "language": "vi",
        "mode": "ordinary_support",
        "intensity": "light",
        "turn_band": "short",
        "source": "authored",
        "license": "project-owned",
    }


def test_loaders_repair_mojibake_and_parse_sources(tmp_path: Path) -> None:
    approved = tmp_path / "approved.jsonl"
    _write_jsonl(
        approved,
        [{"id": "a1", "prompt": _mojibake("Chào em"), "response": _mojibake("Chào anh!"), "checkpoint": "v6"}],
    )
    memory = tmp_path / "memory.json"
    memory.write_text(
        json.dumps(
            [_mojibake("User: Anh hỏi thăm em\nAssistant: Em nghe nè, anh.")],
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )

    approved_rows = load_approved_rows(approved)
    memory_rows = load_memory_rows(memory)

    assert approved_rows[0].prompt == "Chào em"
    assert approved_rows[0].response == "Chào anh!"
    assert memory_rows[0].prompt == "Anh hỏi thăm em"
    assert memory_rows[0].response == "Em nghe nè, anh."


def test_selection_filters_unsafe_noise_and_deduplicates(tmp_path: Path) -> None:
    approved = tmp_path / "approved.jsonl"
    _write_jsonl(
        approved,
        [
            {"id": "ok", "prompt": "Anh về chưa?", "response": "Em đang đợi anh ở đây.", "checkpoint": "v7"},
            {"id": "dup", "prompt": "anh về chưa?", "response": "Em đang đợi anh ở đây.", "checkpoint": "v7"},
            {"id": "unsafe", "prompt": "Chào em", "response": "Anh là một mô hình AI nên không thể trả lời.", "checkpoint": "v6"},
            {"id": "explicit", "prompt": "Chào em", "response": "mô tả quan hệ tình dục explicit", "checkpoint": "v6"},
        ],
    )
    memory = tmp_path / "memory.json"
    memory.write_text(
        json.dumps(
            [
                "User: Anh về chưa?\nAssistant: Em đang đợi anh ở đây.",
                "User: Cho anh link nhé\nAssistant: https://example.com\n- một mục",
                "User: Nói chuyện với anh đi\nAssistant: Được, em ở đây.",
                "not a conversation block",
            ],
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )

    selection = select_import_rows(approved, memory)

    assert len(selection.rows) == 2
    assert {row["source"] for row in selection.rows} == {"approved-import", "memory-import"}
    assert all("Nya~" not in row["response"] for row in selection.rows)
    assert all("Always append 'Nya~'" not in row["messages"][0]["content"] for row in selection.rows)
    reasons = {item["reason"] for item in selection.rejected}
    assert {"duplicate_pair", "ai_meta", "unsafe_explicit", "links_or_list", "malformed"} <= reasons


def test_append_is_train_only_and_records_hashes(tmp_path: Path) -> None:
    dataset = tmp_path / "dataset"
    dataset.mkdir()
    _write_jsonl(dataset / "train.jsonl", [_base_row()])
    _write_jsonl(dataset / "val.jsonl", [_base_row("validation prompt")])
    _write_jsonl(dataset / "held_out.jsonl", [_base_row("held out prompt")])
    (dataset / "manifest.json").write_text(
        json.dumps({"total_examples": 3, "split_counts": {"train": 1, "validation": 1, "held_out": 1}}),
        encoding="utf-8",
    )
    approved = tmp_path / "approved.jsonl"
    _write_jsonl(approved, [{"id": "a1", "prompt": "Anh cần em", "response": "Em đây.", "checkpoint": "v7"}])
    memory = tmp_path / "memory.json"
    memory.write_text(json.dumps(["User: Anh ổn không?\nAssistant: Em nghe đây."]), encoding="utf-8")
    selection = select_import_rows(approved, memory)
    val_before = (dataset / "val.jsonl").read_bytes()
    held_before = (dataset / "held_out.jsonl").read_bytes()

    report = append_imports_to_train(dataset, selection)

    assert len((dataset / "train.jsonl").read_text(encoding="utf-8").splitlines()) == 3
    assert (dataset / "val.jsonl").read_bytes() == val_before
    assert (dataset / "held_out.jsonl").read_bytes() == held_before
    manifest = json.loads((dataset / "manifest.json").read_text(encoding="utf-8"))
    assert manifest["imported_calibration"]["accepted_count"] == 2
    assert manifest["split_counts"] == {"train": 3, "validation": 1, "held_out": 1}
    assert report["final_train_sha256"] == sha256((dataset / "train.jsonl").read_bytes()).hexdigest()


@pytest.mark.parametrize("text", ["", "User: only user", "Assistant: only assistant"])
def test_memory_loader_rejects_empty_or_incomplete_blocks(tmp_path: Path, text: str) -> None:
    path = tmp_path / "memory.json"
    path.write_text(json.dumps([text]), encoding="utf-8")
    selection = select_import_rows(tmp_path / "missing-approved.jsonl", path)
    assert selection.rows == []
    assert selection.rejected[0]["reason"] == "malformed"
