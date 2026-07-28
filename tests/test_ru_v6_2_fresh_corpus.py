from __future__ import annotations

import json
from pathlib import Path

from mini_gpt.ru_v6_2_fresh_corpus import build_ru_v6_2_fresh_corpus


def test_fresh_core_shape_and_safe_split(tmp_path: Path) -> None:
    report = build_ru_v6_2_fresh_corpus(tmp_path / "fresh", write=False)
    assert report["authored_total_examples"] == 1200
    assert report["split_counts"] == {"train": 720, "validation": 240, "held_out": 240}
    assert report["quality_gates"]["safe_non_explicit"] is True


def test_fresh_build_merges_imports_into_train_only(tmp_path: Path) -> None:
    approved = tmp_path / "approved.jsonl"
    approved.write_text(
        json.dumps({"id": "a1", "prompt": "Anh cần em", "response": "Em đây, anh nói đi.", "checkpoint": "v7"}) + "\n",
        encoding="utf-8",
    )
    memory = tmp_path / "memory.json"
    memory.write_text(json.dumps(["User: Anh ổn không?\nAssistant: Em nghe đây."]), encoding="utf-8")
    destination = tmp_path / "fresh"

    report = build_ru_v6_2_fresh_corpus(destination, approved_file=approved, memory_file=memory)

    assert report["authored_total_examples"] == 1200
    assert report["imported_calibration"]["accepted_count"] == 2
    assert report["split_counts"] == {"train": 722, "validation": 240, "held_out": 240}
    assert len((destination / "train.jsonl").read_text(encoding="utf-8").splitlines()) == 722
