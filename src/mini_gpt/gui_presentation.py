from __future__ import annotations


PALETTE = {
    "background": "#FAF8FC",
    "surface": "#FFFFFF",
    "ink": "#2E2933",
    "muted": "#6E6575",
    "accent": "#765E98",
    "ru_bubble": "#F0E9F8",
    "user_bubble": "#E9F1F0",
}


def format_elapsed(seconds: float) -> str:
    """Format a measured local-runtime duration for the compact GUI status line."""
    if seconds < 0.1:
        return "under 0.1 s"
    if seconds < 60:
        return f"{seconds:.1f} s"
    whole_seconds = int(round(seconds))
    return f"{whole_seconds // 60} min {whole_seconds % 60} s"


def format_generation_status(*, cache_hit: bool, seconds: float) -> str:
    suffix = "" if cache_hit else " (model loaded)"
    return f"Ru replied in {format_elapsed(seconds)}{suffix}"
