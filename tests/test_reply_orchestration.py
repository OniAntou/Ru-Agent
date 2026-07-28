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
