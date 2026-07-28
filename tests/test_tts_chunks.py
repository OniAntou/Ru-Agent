from mini_gpt.tts_chunks import split_tts_chunks


def test_split_tts_chunks_keeps_sentences_in_order() -> None:
    assert split_tts_chunks("Em chao anh. Hom nay em khoe!", max_chars=80) == [
        "Em chao anh.",
        "Hom nay em khoe!",
    ]


def test_split_tts_chunks_splits_long_sentence_at_word_boundary() -> None:
    chunks = split_tts_chunks("mot " * 40, max_chars=40)

    assert chunks[0] == "mot mot mot mot mot mot mot mot mot mot"
    assert all(len(chunk) <= 40 for chunk in chunks)
    assert " ".join(chunks).split() == ("mot " * 40).split()


def test_split_tts_chunks_discards_empty_segments() -> None:
    assert split_tts_chunks("   \n\n  ", max_chars=80) == []
