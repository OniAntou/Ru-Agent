import numpy as np
import pytest

from mini_gpt.data import TokenWindowDataset, encode_split, prepare_corpus
from mini_gpt.tokenizer import train_tokenizer


def test_prepare_corpus_deduplicates_and_splits_deterministically(tmp_path):
    raw = tmp_path / "raw"
    processed = tmp_path / "processed"
    raw.mkdir()
    (raw / "demo.txt").write_text(
        "VI: Xin chào Việt Nam.\n\nEN: Hello from England.\n\nVI: Dữ liệu sạch giúp mô hình học tốt.\n\nEN: Clean data helps the model learn.\n\nEN: Clean data helps the model learn.",
        encoding="utf-8",
    )

    report = prepare_corpus(raw, processed, validation_ratio=0.25, seed=7)
    assert report.documents_before_deduplication == 5
    assert report.documents_after_deduplication == 4
    first_train = (processed / "train.txt").read_text(encoding="utf-8")

    other_processed = tmp_path / "processed-again"
    prepare_corpus(raw, other_processed, validation_ratio=0.25, seed=7)
    assert first_train == (other_processed / "train.txt").read_text(encoding="utf-8")


def test_encoded_split_returns_shifted_windows(tmp_path):
    text = tmp_path / "train.txt"
    text.write_text("hello world\n\nXin chào Việt Nam", encoding="utf-8")
    tokenizer_path = tmp_path / "tokenizer.json"
    train_tokenizer([text], tokenizer_path, vocab_size=300)
    tokens_path = tmp_path / "train.bin"
    token_count = encode_split(text, tokenizer_path, tokens_path)

    dataset = TokenWindowDataset(tokens_path, context_length=4)
    inputs, targets = dataset[0]
    raw = np.fromfile(tokens_path, dtype=np.int32)
    assert token_count == len(raw)
    assert len(dataset) == token_count - 4
    assert inputs.tolist() == raw[:4].tolist()
    assert targets.tolist() == raw[1:5].tolist()


def test_dataset_rejects_short_token_file(tmp_path):
    path = tmp_path / "short.bin"
    np.asarray([1, 2, 3], dtype=np.int32).tofile(path)
    with pytest.raises(ValueError, match="more tokens"):
        TokenWindowDataset(path, context_length=3)

