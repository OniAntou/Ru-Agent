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


def conversation_display_segments(prompt: str, response: str) -> tuple[tuple[str, str], ...]:
    """Return one completed chat turn in the order it happened."""
    return (
        ("You\n", "user_label"),
        (f"{prompt}\n\n", "user_bubble"),
        ("Ru\n", "ru_label"),
        (f"{response}\n\n", "ru_bubble"),
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
    gwen_warm_seconds: float | None = None,
    first_voice_seconds: float | None = None,
) -> str:
    suffix = "" if cache_hit else " (model loaded)"
    phases = []
    if api_seconds is not None:
        phases.append(f"API {format_elapsed(api_seconds)}")
    if gwen_warm_seconds is not None:
        phases.append(f"Gwen warm {format_elapsed(gwen_warm_seconds)}")
    if first_voice_seconds is not None:
        phases.append(f"first voice {format_elapsed(first_voice_seconds)}")
    return " | ".join((f"Ru replied in {format_elapsed(seconds)}{suffix}", *phases))
