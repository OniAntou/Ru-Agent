from __future__ import annotations

from mini_gpt.reply_orchestration import execute_reply


def test_execute_reply_resets_stream_before_a_grounded_follow_up() -> None:
    replies = iter(("[SEARCH: weather]", "Troi dep day anh."))
    events: list[tuple[str, object]] = []

    def generate(_messages, *_settings, on_stream_delta=None, **_kwargs):
        reply = next(replies)
        if on_stream_delta is not None:
            on_stream_delta(reply)
        return reply

    result = execute_reply(
        generate,
        prompt="Thoi tiet sao?",
        system_instruction="Ru system",
        max_new_tokens=32,
        temperature=0.7,
        top_k=20,
        allow_web_search=True,
        web_search=lambda query: f"ket qua cho {query}",
        on_stream_delta=lambda delta: events.append(("delta", delta)),
        on_stream_reset=lambda: events.append(("reset", None)),
    )

    assert result == "Troi dep day anh."
    assert events == [
        ("delta", "[SEARCH: weather]"),
        ("reset", None),
        ("delta", "Troi dep day anh."),
    ]


def test_search_marker_passed_through_when_search_disabled() -> None:
    """When allow_web_search=False, [SEARCH: ...] is returned as-is."""
    def generate(_messages, *_settings, **_kwargs):
        return "[SEARCH: weather] Xin chào anh."

    result = execute_reply(
        generate,
        prompt="Thoi tiet?",
        system_instruction="Ru",
        max_new_tokens=32,
        temperature=0.7,
        top_k=20,
        allow_web_search=False,
    )
    assert result == "[SEARCH: weather] Xin chào anh."


def test_search_error_returns_graceful_fallback() -> None:
    """When web_search raises, the pipeline continues with fallback text."""
    call_count = 0

    def generate(_messages, *_settings, **_kwargs):
        nonlocal call_count
        call_count += 1
        if call_count == 1:
            return "[SEARCH: hacker news]"
        return f"Generated after search failure"

    def failing_search(query: str) -> str:
        raise ConnectionError("network down")

    result = execute_reply(
        generate,
        prompt="tin tức mới?",
        system_instruction="Ru",
        max_new_tokens=32,
        temperature=0.7,
        top_k=20,
        allow_web_search=True,
        web_search=failing_search,
    )
    assert result == "Generated after search failure"
    assert call_count == 2


def test_empty_search_query_in_marker_skips_search() -> None:
    """[SEARCH: ] or [SEARCH:] with empty query → passed through without searching."""
    def generate(_messages, *_settings, **_kwargs):
        return "[SEARCH:] nothing here"

    search_called = False

    def search(q: str) -> str:
        nonlocal search_called
        search_called = True
        return "should not reach here"

    result = execute_reply(
        generate,
        prompt="hi",
        system_instruction="Ru",
        max_new_tokens=32,
        temperature=0.7,
        top_k=20,
        allow_web_search=True,
        web_search=search,
    )
    assert result == "[SEARCH:] nothing here"
    assert search_called is False


def test_normal_response_without_search_marker() -> None:
    """No [SEARCH: ...] marker → single generation, no search."""
    def generate(_messages, *_settings, **_kwargs):
        return "Xin chào anh, hôm nay thế nào?"

    result = execute_reply(
        generate,
        prompt="hello",
        system_instruction="Ru",
        max_new_tokens=32,
        temperature=0.7,
        top_k=20,
        allow_web_search=True,
    )
    assert result == "Xin chào anh, hôm nay thế nào?"
