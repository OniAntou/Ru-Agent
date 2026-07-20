from mini_gpt.tokenizer import encode_text, load_tokenizer, train_tokenizer


def test_byte_bpe_round_trip_preserves_vietnamese_and_english(tmp_path):
    source = tmp_path / "corpus.txt"
    source.write_text("Xin chào Việt Nam. Hello English.\nHọc máy là thú vị.", encoding="utf-8")
    output = tmp_path / "tokenizer.json"

    actual_size = train_tokenizer([source], output, vocab_size=300)
    tokenizer = load_tokenizer(output)
    decoded = tokenizer.decode(encode_text(tokenizer, "Xin chào Việt Nam. Hello English."))

    assert actual_size == tokenizer.get_vocab_size()
    assert "Việt" in decoded
    assert "Hello" in decoded

