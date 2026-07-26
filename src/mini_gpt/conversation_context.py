from __future__ import annotations

from collections.abc import Sequence

from .chat import ChatTurn


def build_conversation_messages(
    system_prompt: str,
    prompt: str,
    turns: Sequence[ChatTurn],
    *,
    memory_context: str | None = None,
    max_history_chars: int = 12_000,
) -> tuple[tuple[str, str], ...]:
    if max_history_chars < 0:
        raise ValueError("max history chars cannot be negative")

    messages: list[tuple[str, str]] = [("system", system_prompt)]
    if memory_context:
        messages.append(
            ("system", f"Past memories:\n{memory_context}\n\nUse this only when relevant.")
        )

    retained: list[ChatTurn] = []
    used_chars = 0
    for turn in reversed(turns):
        turn_chars = len(turn.prompt) + len(turn.response)
        if turn_chars > max_history_chars:
            continue
        if used_chars + turn_chars > max_history_chars:
            break
        retained.append(turn)
        used_chars += turn_chars

    for turn in reversed(retained):
        messages.extend((("user", turn.prompt), ("assistant", turn.response)))
    messages.append(("user", prompt))
    return tuple(messages)
