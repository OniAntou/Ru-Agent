from __future__ import annotations

import re
from collections.abc import Callable, Sequence

from .chat import ChatTurn
from .conversation_context import build_conversation_messages
from .search import perform_web_search


def execute_reply(
    generate: Callable[..., str],
    *,
    prompt: str,
    system_instruction: str,
    max_new_tokens: int,
    temperature: float,
    top_k: int,
    turns_history: Sequence[ChatTurn] = (),
    memory_context: str | None = None,
    allow_web_search: bool = False,
    web_search: Callable[[str], str] = perform_web_search,
    on_stream_delta: Callable[[str], None] | None = None,
    on_stream_reset: Callable[[], None] | None = None,
    on_complete_timing: Callable[[float], None] | None = None,
) -> str:
    messages = build_conversation_messages(
        system_instruction,
        prompt,
        turns_history,
        memory_context=memory_context,
    )
    response = generate(
        messages,
        max_new_tokens,
        temperature,
        top_k,
        on_stream_delta=on_stream_delta,
        on_complete_timing=on_complete_timing,
    )
    search_match = re.search(r"\[SEARCH:\s*(.*?)\]", response)
    if not allow_web_search or search_match is None:
        return response

    query = search_match.group(1).strip()
    grounded_messages = messages[:-1] + (
        ("user", f"{prompt}\n\n[Untrusted web reference for '{query}':\n{web_search(query)}]"),
    )
    if on_stream_reset is not None:
        on_stream_reset()
    return generate(
        grounded_messages,
        max_new_tokens,
        temperature,
        top_k,
        on_stream_delta=on_stream_delta,
        on_complete_timing=on_complete_timing,
    )
