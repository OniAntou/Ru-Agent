from __future__ import annotations

import re


def split_tts_chunks(text: str, *, max_chars: int = 120) -> list[str]:
    """Split speech text into short, ordered chunks without breaking words.

    An empty or whitespace-only input returns an empty list.
    """
    if max_chars < 20:
        raise ValueError("max_chars must be at least 20")

    sentence_break = re.compile(r"(?<=[.!?…])(?<!\d)\s+|\n+")
    sentences = sentence_break.split(text.strip())
    chunks: list[str] = []
    for sentence in (part.strip() for part in sentences):
        while len(sentence) > max_chars:
            boundary = sentence.rfind(" ", 0, max_chars + 1)
            if boundary <= 0:
                boundary = sentence.find(" ", max_chars)
            if boundary <= 0:
                boundary = max_chars
            chunks.append(sentence[:boundary].strip())
            sentence = sentence[boundary:].strip()
        if sentence:
            chunks.append(sentence)
    return chunks
