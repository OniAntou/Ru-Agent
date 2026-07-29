from __future__ import annotations

import json
from urllib.request import Request

import mini_gpt.openai_runtime as runtime_module
from mini_gpt.gui_presentation import append_stream_text
from mini_gpt.openai_runtime import OpenAIRuntime


class FakeStreamResponse:
    def __init__(self, events: list[dict[str, object]]) -> None:
        self._lines = [
            f"data: {json.dumps(event)}\n".encode("utf-8") for event in events
        ] + [b"data: [DONE]\n"]

    def __enter__(self) -> FakeStreamResponse:
        return self

    def __exit__(self, *_args: object) -> None:
        return None

    def __iter__(self):
        return iter(self._lines)


def test_stream_callback_receives_only_new_text_for_cumulative_provider_events(monkeypatch) -> None:
    stream = FakeStreamResponse(
        [
            {"choices": [{"delta": {"content": "Ch"}}]},
            {"choices": [{"delta": {"content": "Chào"}}]},
            {"choices": [{"delta": {"content": "Chào anh"}}]},
        ]
    )
    monkeypatch.setattr(runtime_module, "urlopen", lambda _request, timeout: stream)
    deltas: list[str] = []

    response = OpenAIRuntime("key")._read_stream(
        Request("https://example.test/stream"), deltas.append
    )

    assert response == "Chào anh"
    assert deltas == ["Ch", "ào", " anh"]
    visible = ""
    for delta in deltas:
        visible = append_stream_text(visible, delta)
    assert visible == response
