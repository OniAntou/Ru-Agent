from __future__ import annotations

from ru_runtime.tts_chunks import split_tts_chunks


def test_split_tts_chunks_handles_unicode_ellipsis_in_sentence_order() -> None:
    chunks = split_tts_chunks("Chậm thôi… Mình kiểm tra từng bước nhé.")

    assert chunks == ["Chậm thôi…", "Mình kiểm tra từng bước nhé."]


def test_split_tts_chunks_preserves_word_boundaries_when_wrapping() -> None:
    chunks = split_tts_chunks("Một câu ngắn. Đây là câu thứ hai rất dài.", max_chars=20)

    assert chunks == ["Một câu ngắn.", "Đây là câu thứ hai", "rất dài."]
