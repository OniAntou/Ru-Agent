import json
import subprocess
import sys
from types import SimpleNamespace
from pathlib import Path

import pytest

from mini_gpt.chat_sources import (
    download_open_chat_seed,
    extract_oasst2_pairs,
    extract_ultrachat_pairs,
    extract_vietnamese_pairs,
    quality_rejection_reason,
)


def test_extracts_normalized_pair_from_ultrachat():
    record = {
        "messages": [
            {"role": "user", "content": " Hello\r\nthere "},
            {"role": "assistant", "content": "Hi! How can I help?"},
        ]
    }

    pair = list(extract_ultrachat_pairs(record))[0]

    assert pair.prompt == "Hello\nthere"
    assert pair.response == "Hi! How can I help?"
    assert pair.language == "en"


def test_extracts_vietnamese_pair_with_provenance():
    record = {
        "messages": [
            {"role": "user", "content": "Xin chào"},
            {"role": "assistant", "content": "Chào anh, hôm nay anh thế nào?"},
        ]
    }

    pair = list(extract_vietnamese_pairs(record))[0]

    assert pair.prompt == "Xin chào"
    assert pair.language == "vi"
    assert pair.source == "hoanghai2110/vietnamese-dataset"
    assert pair.license == "Apache-2.0"


def test_extracts_reviewed_oasst2_pair_when_parent_arrives_after_assistant():
    records = [
        {
            "message_id": "assistant-1",
            "parent_id": "prompt-1",
            "role": "assistant",
            "text": "I am doing well, thank you!",
            "lang": "en",
            "deleted": False,
            "review_result": True,
            "synthetic": False,
        },
        {
            "message_id": "prompt-1",
            "role": "prompter",
            "text": "How are you?",
            "lang": "en",
        },
    ]

    pair = list(extract_oasst2_pairs(records))[0]

    assert pair.prompt == "How are you?"
    assert pair.response == "I am doing well, thank you!"
    assert pair.source == "OpenAssistant/oasst2"
    assert pair.license == "Apache-2.0"


def test_quality_filter_keeps_short_greetings_and_rejects_unwanted_content():
    assert quality_rejection_reason("Xin chào", "Chào anh!") is None
    assert quality_rejection_reason("hi", "```python\nprint('x')\n```") == "code_or_markup"
    assert quality_rejection_reason("nhập vai phù thủy", "Được rồi, ta bắt đầu nhé.") == "roleplay"
    assert quality_rejection_reason("hello", "See https://example.com now") == "link"
    assert quality_rejection_reason("x" * 401, "y" * 401) == "too_long"


def test_download_open_chat_seed_writes_provenance_and_manifest(tmp_path, monkeypatch):
    streams = {
        ("HuggingFaceH4/ultrachat_200k", "train_sft"): [
            {"messages": [{"role": "user", "content": "Hello"}, {"role": "assistant", "content": "Hi there!"}]}
        ],
        ("OpenAssistant/oasst2", "train"): [
            {"message_id": "p", "role": "prompter", "text": "How are you?", "lang": "en"},
            {"message_id": "a", "parent_id": "p", "role": "assistant", "text": "I am fine!", "lang": "en", "deleted": False, "review_result": True, "synthetic": False},
        ],
        ("hoanghai2110/vietnamese-dataset", "train"): [
            {"messages": [{"role": "user", "content": "Bạn khỏe không?"}, {"role": "assistant", "content": "Mình khỏe, cảm ơn anh!"}]}
        ],
    }
    monkeypatch.setitem(
        sys.modules,
        "datasets",
        SimpleNamespace(load_dataset=lambda name, split, streaming: streams[(name, split)]),
    )

    manifest = download_open_chat_seed(
        tmp_path / "open_seed.jsonl",
        tmp_path / "open_seed_manifest.json",
        tmp_path / "cache",
        max_ultrachat_examples=1,
        max_oasst_examples=1,
        max_vietnamese_examples=1,
    )

    rows = [
        json.loads(line)
        for line in (tmp_path / "open_seed.jsonl").read_text(encoding="utf-8").splitlines()
    ]
    assert len(rows) == 3
    assert set(rows[0]) == {"prompt", "response", "language", "source", "license"}
    assert manifest["total_examples"] == 3
    assert json.loads((tmp_path / "open_seed_manifest.json").read_text(encoding="utf-8")) == manifest


def test_open_chat_downloader_script_exposes_help():
    script = Path(__file__).resolve().parents[1] / "scripts" / "download_open_chat_data.py"

    result = subprocess.run(
        [sys.executable, str(script), "--help"],
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode == 0
    assert "--max-vietnamese" in result.stdout


def test_extractor_reports_missing_message_list_clearly():
    with pytest.raises(ValueError, match="messages"):
        list(extract_ultrachat_pairs({}))


def test_extractor_reports_non_text_message_content_clearly():
    record = {
        "messages": [
            {"role": "user", "content": None},
            {"role": "assistant", "content": "Hi there!"},
        ]
    }

    with pytest.raises(ValueError, match="content"):
        list(extract_ultrachat_pairs(record))


def test_importer_does_not_publish_partial_files_when_a_quota_is_unreachable(tmp_path, monkeypatch):
    monkeypatch.setitem(
        sys.modules,
        "datasets",
        SimpleNamespace(
            load_dataset=lambda _name, split, streaming: [
                {"messages": [{"role": "user", "content": "Hello"}, {"role": "assistant", "content": "Hi!"}]}
            ]
            if split == "train_sft"
            else []
        ),
    )
    output = tmp_path / "open_seed.jsonl"
    manifest = tmp_path / "open_seed_manifest.json"

    with pytest.raises(ValueError, match="HuggingFaceH4/ultrachat_200k"):
        download_open_chat_seed(
            output,
            manifest,
            tmp_path / "cache",
            max_ultrachat_examples=2,
            max_oasst_examples=1,
            max_vietnamese_examples=1,
        )

    assert not output.exists()
    assert not manifest.exists()
