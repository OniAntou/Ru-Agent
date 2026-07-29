from __future__ import annotations

from pathlib import Path

from mini_gpt.ru_v6_3_corpus import build_ru_v6_3_corpus


def test_v6_3_builder_writes_bounded_deterministic_splits(tmp_path: Path) -> None:
    report = build_ru_v6_3_corpus(tmp_path / "v6_3", write=True)
    assert report["authored_total_examples"] == 180
    assert report["split_counts"] == {"train": 108, "validation": 36, "held_out": 36}
    assert report["quality_gates"]["training_started"] is False
    assert report["quality_gates"]["bounded_contextual_profanity"] is True
    assert report["profanity_counts"]["mild"] > 0
    assert report["profanity_counts"]["medium"] > 0
    assert (tmp_path / "v6_3" / "manifest.json").is_file()
    assert len((tmp_path / "v6_3" / "train.jsonl").read_text(encoding="utf-8").splitlines()) == 108


def test_v6_3_audit_does_not_write(tmp_path: Path) -> None:
    destination = tmp_path / "v6_3"
    report = build_ru_v6_3_corpus(destination, write=False)
    assert report["total_examples"] == 180
    assert not destination.exists()
