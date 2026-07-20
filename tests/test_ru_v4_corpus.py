import json
import subprocess
import sys
from copy import deepcopy
from pathlib import Path

import pytest

from mini_gpt.ru_v4_corpus import build_ru_v4_corpus
from mini_gpt.ru_v4_dialogues import v4_dialogue_records

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "build_ru_qwen_v4_data.py"


def test_v4_dialogue_source_has_the_approved_language_and_mode_counts():
    rows = v4_dialogue_records()

    assert len(rows) == 400
    assert {language: sum(row["language"] == language for row in rows) for language in ("vi", "en", "mixed")} == {
        "vi": 160,
        "en": 160,
        "mixed": 80,
    }
    assert {mode: sum(row["mode"] == mode for row in rows) for mode in ("acknowledge", "follow_up", "everyday_help", "story", "light_humour", "correction")} == {
        "acknowledge": 100,
        "follow_up": 100,
        "everyday_help": 60,
        "story": 60,
        "light_humour": 40,
        "correction": 40,
    }


def test_v4_builder_writes_disjoint_train_validation_and_held_out_splits(tmp_path):
    report = build_ru_v4_corpus(v4_dialogue_records(), tmp_path, seed=17)

    assert report["split_counts"] == {"train": 320, "validation": 40, "held_out": 40}
    assert report["contextual_examples"] >= 120
    assert report["direct_acknowledgements"] >= 100
    assert report["everyday_help_examples"] >= 60
    rows = [
        json.loads(line)
        for filename in ("train.jsonl", "val.jsonl", "held_out.jsonl")
        for line in (tmp_path / filename).read_text(encoding="utf-8").splitlines()
    ]
    keys = {json.dumps({"messages": row["messages"], "response": row["response"]}, ensure_ascii=False, sort_keys=True) for row in rows}
    assert len(rows) == len(keys) == 400


def test_v4_corpus_cli_exposes_local_audit_without_external_input_flags():
    result = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True, check=False)

    assert result.returncode == 0
    assert "Build Ru's local V4 curated conversation corpus" in result.stdout
    assert "--audit" in result.stdout
    assert "--input" not in result.stdout


def test_v4_builder_rejects_duplicates_invalid_modes_and_wrong_vietnamese_voice(tmp_path):
    duplicate_rows = v4_dialogue_records()
    duplicate_rows[159] = deepcopy(duplicate_rows[0])
    with pytest.raises(ValueError, match="duplicate"):
        build_ru_v4_corpus(duplicate_rows, tmp_path / "duplicates")

    invalid_mode_rows = v4_dialogue_records()
    invalid_mode_rows[0] = {**invalid_mode_rows[0], "mode": "lecture"}
    with pytest.raises(ValueError, match="unsupported response mode"):
        build_ru_v4_corpus(invalid_mode_rows, tmp_path / "mode")

    wrong_voice_rows = v4_dialogue_records()
    wrong_voice_rows[0] = {**wrong_voice_rows[0], "response": "Tôi hiểu rồi, bạn nên nghỉ."}
    with pytest.raises(ValueError, match="default em/anh voice"):
        build_ru_v4_corpus(wrong_voice_rows, tmp_path / "voice")
