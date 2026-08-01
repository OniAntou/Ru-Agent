from __future__ import annotations

import unicodedata

_PRIVATE_STREAM_MARKERS = ("[[vts:", "[[voice_pace:", "[search:")


def normalize_text(text: str) -> str:
    return unicodedata.normalize("NFC", text.replace("\r\n", "\n").replace("\r", "\n")).strip()


def append_stream_text(existing: str, incoming: str) -> str:
    """Accept either an incremental chunk or a provider's cumulative snapshot."""
    if incoming.startswith(existing):
        return incoming
    if existing.startswith(incoming):
        return existing
    overlap = 0
    for length in range(min(len(existing), len(incoming)), 0, -1):
        if existing[-length:] == incoming[:length]:
            overlap = length
            break
    if overlap:
        return existing + incoming[overlap:]
    return existing + incoming


def safe_streamed_text(text: str) -> str:
    """Return only text that cannot be part of private control metadata."""
    folded = text.casefold()
    complete_positions = [
        position
        for marker in _PRIVATE_STREAM_MARKERS
        for position in (folded.find(marker),)
        if position >= 0
    ]
    if complete_positions:
        return text[: min(complete_positions)]
    hold = 0
    for marker in _PRIVATE_STREAM_MARKERS:
        for length in range(1, min(len(marker), len(folded)) + 1):
            if folded.endswith(marker[:length]):
                hold = max(hold, length)
    return text[:-hold] if hold else text
