import json

import pytest

from mini_gpt.chat_finetune_data import prepare_chat_corpus


def _write_jsonl(path, records):
    path.write_text(
        "\n".join(json.dumps(record, ensure_ascii=False) for record in records) + "\n",
        encoding="utf-8",
    )


def test_prepare_chat_corpus_deduplicates_and_preserves_vietnamese(tmp_path):
    source = tmp_path / "approved.jsonl"
    _write_jsonl(
        source,
        [
            {"prompt": "Xin chào", "response": "Chào anh"},
            {"prompt": "Xin chào", "response": "Chào anh"},
            {"prompt": "How are you?", "response": "I am well."},
            {"prompt": "Cảm ơn", "response": "Rất vui được giúp anh."},
        ],
    )

    report = prepare_chat_corpus(source, tmp_path / "processed", validation_ratio=0.5)

    output = (tmp_path / "processed" / "train.txt").read_text(encoding="utf-8")
    output += (tmp_path / "processed" / "val.txt").read_text(encoding="utf-8")
    assert report.examples_before_deduplication == 4
    assert report.examples_after_deduplication == 3
    assert report.train_examples == 1
    assert report.validation_examples == 2
    assert "User: Xin chào\nAssistant: Chào anh" in output
    assert "User: Cảm ơn\nAssistant: Rất vui được giúp anh." in output


def test_prepare_chat_corpus_split_is_deterministic(tmp_path):
    source = tmp_path / "approved.jsonl"
    _write_jsonl(
        source,
        [{"prompt": f"prompt {index}", "response": f"response {index}"} for index in range(8)],
    )

    prepare_chat_corpus(source, tmp_path / "first", seed=7)
    prepare_chat_corpus(source, tmp_path / "second", seed=7)

    for name in ("train.txt", "val.txt", "report.json"):
        assert (tmp_path / "first" / name).read_bytes() == (tmp_path / "second" / name).read_bytes()


@pytest.mark.parametrize(
    ("records", "message"),
    [
        ([{"prompt": "", "response": "answer"}, {"prompt": "q", "response": "a"}], "prompt"),
        ([{"prompt": "question", "response": " "}, {"prompt": "q", "response": "a"}], "response"),
    ],
)
def test_prepare_chat_corpus_rejects_blank_turn_parts(tmp_path, records, message):
    source = tmp_path / "approved.jsonl"
    _write_jsonl(source, records)

    with pytest.raises(ValueError, match=message):
        prepare_chat_corpus(source, tmp_path / "processed")


def test_prepare_chat_corpus_rejects_malformed_json_and_too_few_unique_pairs(tmp_path):
    malformed = tmp_path / "malformed.jsonl"
    malformed.write_text('{"prompt":"q","response":"a"}\nnot-json\n', encoding="utf-8")
    with pytest.raises(ValueError, match="line 2"):
        prepare_chat_corpus(malformed, tmp_path / "malformed-output")

    duplicate = tmp_path / "duplicate.jsonl"
    _write_jsonl(duplicate, [{"prompt": "q", "response": "a"}] * 2)
    with pytest.raises(ValueError, match="at least two unique"):
        prepare_chat_corpus(duplicate, tmp_path / "duplicate-output")
