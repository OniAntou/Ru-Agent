from __future__ import annotations

from collections.abc import Sequence

from .chat import ChatTurn


def _truncate_turn(turn: ChatTurn, max_chars: int) -> ChatTurn | None:
    """Cut a too-large turn down to *max_chars*, keeping the response first."""
    if max_chars <= 0:
        return None
    response = turn.response[: max_chars // 2]
    prompt = turn.prompt[: max_chars - len(response)]
    if not prompt or not response:
        return None
    return ChatTurn(prompt=prompt, response=response)


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
            (
                "system",
                "Relevant saved memory:\n"
                f"{memory_context}\n\n"
                "Use it only when relevant; do not claim to remember anything not listed.",
            )
        )

    retained: list[ChatTurn] = []
    used_chars = 0
    for turn in reversed(turns):
        turn_chars = len(turn.prompt) + len(turn.response)
        if used_chars + turn_chars <= max_history_chars:
            retained.append(turn)
            used_chars += turn_chars
            continue
        if retained:
            break
        truncated = _truncate_turn(turn, max_history_chars)
        if truncated is not None:
            retained.append(truncated)
            used_chars = len(truncated.prompt) + len(truncated.response)
        break

    for turn in reversed(retained):
        messages.extend((("user", turn.prompt), ("assistant", turn.response)))
    messages.append(("user", prompt))
    return tuple(messages)
