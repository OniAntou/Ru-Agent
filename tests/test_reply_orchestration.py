from __future__ import annotations

from ru_runtime.reply_orchestration import execute_reply


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


def test_search_marker_stripped_when_search_disabled() -> None:
    """When allow_web_search=False, the [SEARCH: ...] tag is stripped from the reply."""
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
    assert result == "Xin chào anh."


def test_grounded_follow_up_failure_falls_back_to_initial_response() -> None:
    """If the grounded second generation fails, the initial response is kept."""
    call_count = 0

    def generate(_messages, *_settings, **_kwargs):
        nonlocal call_count
        call_count += 1
        if call_count == 1:
            return "[SEARCH: weather] Trời đẹp."
        raise RuntimeError("second generation failed")

    result = execute_reply(
        generate,
        prompt="Thoi tiet?",
        system_instruction="Ru",
        max_new_tokens=32,
        temperature=0.7,
        top_k=20,
        allow_web_search=True,
        web_search=lambda query: "ket qua",
    )
    assert result == "Trời đẹp."
    assert call_count == 2


def test_search_results_are_sanitized_and_bounded() -> None:
    """Web content is HTML-stripped, bounded, and nested markers removed."""
    huge_result = "x" * 100_000
    seen_content: list[str] = []

    def recording_generate(messages, *_settings, **_kwargs):
        seen_content.append(messages[-1][1])
        return "Phản hồi cuối."

    result = execute_reply(
        recording_generate,
        prompt="tin tức?",
        system_instruction="Ru",
        max_new_tokens=32,
        temperature=0.7,
        top_k=20,
        allow_web_search=True,
        web_search=lambda query: f"<script>alert(1)</script>\n\n<h1>{huge_result}</h1>",
    )
    assert result == "Phản hồi cuối."
    grounded = seen_content[0]
    assert "<script>" not in grounded
    assert "[SEARCH:" not in grounded
    assert len(grounded) < 10_000


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
    """[SEARCH: ] or [SEARCH:] with empty query → marker stripped, no search."""
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
    assert result == "nothing here"
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


def test_unclosed_search_marker_is_passed_through_without_searching() -> None:
    """[SEARCH: with no closing ] → returned as-is, no search."""
    search_called = False

    def generate(_messages, *_settings, **_kwargs):
        return "[SEARCH: weather"

    def search(q: str) -> str:
        nonlocal search_called
        search_called = True
        return "results"

    result = execute_reply(
        generate,
        prompt="Thời tiết?",
        system_instruction="Ru",
        max_new_tokens=32,
        temperature=0.7,
        top_k=20,
        allow_web_search=True,
        web_search=search,
    )
    assert result == "[SEARCH: weather"
    assert search_called is False


def test_search_result_of_none_still_produces_a_grounded_follow_up() -> None:
    """web_search returning None → second generation still runs with a safe reference."""
    messages_seen: list[object] = []

    def generate(_messages, *_settings, **_kwargs):
        messages_seen.append(_messages)
        if len(messages_seen) == 1:
            return "[SEARCH: weather]"
        return "Dự báo: trời nắng."

    def empty_search(query: str) -> None:
        return None

    result = execute_reply(
        generate,
        prompt="Thời tiết?",
        system_instruction="Ru",
        max_new_tokens=32,
        temperature=0.7,
        top_k=20,
        allow_web_search=True,
        web_search=empty_search,
    )
    assert result == "Dự báo: trời nắng."
    assert len(messages_seen) == 2
    grounded_prompt = str(messages_seen[1][-1][1])
    assert "None" in grounded_prompt
