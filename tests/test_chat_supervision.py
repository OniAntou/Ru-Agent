import json

import numpy as np

from mini_gpt.chat_supervision import (
    SupervisedTokenWindowDataset,
    encode_response_chat_split,
    prepare_response_chat_corpus,
)
from mini_gpt.tokenizer import load_tokenizer, train_tokenizer


def _write_jsonl(path, records):
    path.write_text(
        "\n".join(json.dumps(record, ensure_ascii=False) for record in records) + "\n",
        encoding="utf-8",
    )


def test_prepare_response_chat_corpus_deduplicates_prompt_response_pairs(tmp_path):
    source = tmp_path / "seed.jsonl"
    _write_jsonl(
        source,
        [
            {"prompt": "Xin chào", "response": "Chào anh!"},
            {"prompt": "Xin chào", "response": "Chào anh!"},
            {"prompt": "How are you?", "response": "I am well."},
        ],
    )

    report = prepare_response_chat_corpus(source, tmp_path / "processed", validation_ratio=0.5, seed=7)

    assert report.examples_before_deduplication == 3
    assert report.examples_after_deduplication == 2
    assert report.train_examples == 1
    assert report.validation_examples == 1
    assert len((tmp_path / "processed" / "train.jsonl").read_text(encoding="utf-8").splitlines()) == 1
    assert len((tmp_path / "processed" / "val.jsonl").read_text(encoding="utf-8").splitlines()) == 1


def test_response_encoding_masks_prompt_and_scores_only_assistant_completion(tmp_path):
    examples = tmp_path / "train.jsonl"
    _write_jsonl(examples, [{"prompt": "Hello", "response": "Hi there!"}])
    tokenizer_text = tmp_path / "tokenizer.txt"
    tokenizer_text.write_text("User: Hello\nAssistant: Hi there!", encoding="utf-8")
    tokenizer_path = tmp_path / "tokenizer.json"
    train_tokenizer([tokenizer_text], tokenizer_path, vocab_size=300)
    tokens_path = tmp_path / "train.bin"
    labels_path = tmp_path / "train.labels.bin"

    token_count = encode_response_chat_split(examples, tokenizer_path, tokens_path, labels_path)

    tokenizer = load_tokenizer(tokenizer_path)
    bos_id = tokenizer.token_to_id("<bos>")
    eos_id = tokenizer.token_to_id("<eos>")
    prefix = tokenizer.encode("User: Hello\nAssistant:").ids
    completion = tokenizer.encode("Hi there!").ids
    tokens = np.fromfile(tokens_path, dtype=np.int32).tolist()
    labels = np.fromfile(labels_path, dtype=np.int32).tolist()
    assert token_count == len(tokens) == len(labels)
    assert tokens == [bos_id, *prefix, *completion, eos_id]
    assert labels == [-100] * (1 + len(prefix)) + [*completion, eos_id]


def test_supervised_window_dataset_excludes_windows_without_response_labels(tmp_path):
    tokens_path = tmp_path / "tokens.bin"
    labels_path = tmp_path / "labels.bin"
    np.asarray(range(10), dtype=np.int32).tofile(tokens_path)
    np.asarray([-100, -100, -100, -100, -100, -100, 6, 7, 8, 9], dtype=np.int32).tofile(labels_path)

    dataset = SupervisedTokenWindowDataset(tokens_path, labels_path, context_length=3)

    inputs, targets = dataset[0]
    assert len(dataset) == 4
    assert inputs.tolist() == [3, 4, 5]
    assert targets.tolist() == [-100, -100, 6]
