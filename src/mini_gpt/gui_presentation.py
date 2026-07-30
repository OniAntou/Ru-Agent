from __future__ import annotations


_PRIVATE_STREAM_MARKERS = ("[[VTS:", "[[VOICE_PACE:", "[SEARCH:")


def append_stream_text(existing: str, incoming: str) -> str:
    """Accept either an incremental chunk or a provider's cumulative snapshot."""
    if incoming.startswith(existing):
        return incoming
    if existing.startswith(incoming):
        return existing
    return existing + incoming


def safe_streamed_response(raw: str) -> str:
    """Hide private control markers, including a marker still being typed."""
    if not _PRIVATE_STREAM_MARKERS or not raw:
        return raw
    folded = raw.casefold()
    marker_positions = [folded.find(marker.casefold()) for marker in _PRIVATE_STREAM_MARKERS]
    completed_positions = [position for position in marker_positions if position >= 0]
    if completed_positions:
        return raw[: min(completed_positions)]

    for length in range(max(map(len, _PRIVATE_STREAM_MARKERS)) - 1, 0, -1):
        tail = raw[-length:].casefold()
        if any(marker.casefold().startswith(tail) for marker in _PRIVATE_STREAM_MARKERS):
            return raw[:-length]
    return raw


def conversation_display_segments(prompt: str, response: str) -> tuple[tuple[str, str], ...]:
    """Return one completed chat turn in the order it happened."""
    return (
        ("You\n", "user_label"),
        (f"{prompt}\n\n", "user_bubble"),
        ("Ru\n", "ru_label"),
        (f"{response}\n", "ru_bubble"),
    )


def format_elapsed(seconds: float) -> str:
    """Format a measured local-runtime duration for the compact GUI status line."""
    if seconds < 0.1:
        return "under 0.1 s"
    if seconds < 60:
        return f"{seconds:.1f} s"
    whole_seconds = int(round(seconds))
    return f"{whole_seconds // 60} min {whole_seconds % 60} s"


def format_generation_status(
    *,
    cache_hit: bool,
    seconds: float,
    api_seconds: float | None = None,
    voice_warm_seconds: float | None = None,
    first_voice_seconds: float | None = None,
) -> str:
    suffix = "" if cache_hit else " (model loaded)"
    phases = []
    if api_seconds is not None:
        phases.append(f"API {format_elapsed(api_seconds)}")
    if voice_warm_seconds is not None:
        phases.append(f"Voice warm {format_elapsed(voice_warm_seconds)}")
    if first_voice_seconds is not None:
        phases.append(f"first voice {format_elapsed(first_voice_seconds)}")
    return " | ".join((f"Ru replied in {format_elapsed(seconds)}{suffix}", *phases))
