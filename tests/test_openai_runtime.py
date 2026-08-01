from __future__ import annotations

import json
from urllib.request import Request

import pytest

import mini_gpt.openai_runtime as runtime_module
from mini_gpt._text_utils import append_stream_text
from mini_gpt.openai_runtime import OpenAIGenerationCancelled, OpenAIRuntime


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


class EmptyStreamResponse:
    def __enter__(self) -> EmptyStreamResponse:
        return self

    def __exit__(self, *_args: object) -> None:
        return None

    def __iter__(self):
        return iter([b"data: [DONE]\n"])


class MalformedStreamResponse:
    def __enter__(self) -> MalformedStreamResponse:
        return self

    def __exit__(self, *_args: object) -> None:
        return None

    def __iter__(self):
        return iter([b"data: NOT_VALID_JSON\n", b"data: [DONE]\n"])


class ErrorStreamResponse:
    def __enter__(self) -> ErrorStreamResponse:
        return self

    def __exit__(self, *_args: object) -> None:
        return None

    def __iter__(self):
        return iter([b'data: {"error": "rate limit exceeded"}\n', b"data: [DONE]\n"])


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
        lambda: Request("https://example.test/stream"), deltas.append
    )

    assert response == "Chào anh"
    assert deltas == ["Ch", "ào", " anh"]
    visible = ""
    for delta in deltas:
        visible = append_stream_text(visible, delta)
    assert visible == response


def test_empty_stream_raises_runtime_error(monkeypatch) -> None:
    monkeypatch.setattr(runtime_module, "urlopen", lambda _req, timeout: EmptyStreamResponse())
    rt = OpenAIRuntime("key")
    with pytest.raises(RuntimeError, match="empty chat completions"):
        rt.generate([("user", "hello")], max_new_tokens=100, temperature=0.7, top_k=20)


def test_malformed_json_raises_runtime_error(monkeypatch) -> None:
    monkeypatch.setattr(runtime_module, "urlopen", lambda _req, timeout: MalformedStreamResponse())
    rt = OpenAIRuntime("key")
    with pytest.raises(RuntimeError, match="invalid JSON"):
        rt._read_stream(lambda: Request("https://test"), None)


def test_stream_error_from_api_raises_runtime_error(monkeypatch) -> None:
    monkeypatch.setattr(runtime_module, "urlopen", lambda _req, timeout: ErrorStreamResponse())
    rt = OpenAIRuntime("key")
    with pytest.raises(RuntimeError, match="stream error"):
        rt._read_stream(lambda: Request("https://test"), None)


def test_retry_does_not_duplicate_on_delta(monkeypatch) -> None:
    """When first attempt returns empty and retry succeeds, on_delta is only called from retry."""
    call_count = 0

    def fake_open(_req, timeout):
        nonlocal call_count
        call_count += 1
        if call_count == 1:
            return EmptyStreamResponse()
        return FakeStreamResponse([{"choices": [{"delta": {"content": "Hello"}}]}])

    monkeypatch.setattr(runtime_module, "urlopen", fake_open)
    deltas: list[str] = []
    rt = OpenAIRuntime("key")
    # generate() has the retry loop — _read_stream alone does not retry
    result = rt.generate(
        [("user", "hi")],
        max_new_tokens=100,
        temperature=0.7,
        top_k=20,
        on_delta=deltas.append,
    )
    assert result == "Hello"
    # Attempt 0: EmptyStreamResponse → no deltas fired
    # Attempt 1: on_delta suppressed (attempt > 0)
    # → deltas is empty because retry suppresses on_delta
    assert deltas == []


def test_generate_rejects_messages_not_ending_with_user() -> None:
    rt = OpenAIRuntime("key")
    with pytest.raises(ValueError, match="user turn"):
        rt.generate([("assistant", "hi")], max_new_tokens=100, temperature=0.7, top_k=20)


def test_generate_rejects_empty_api_key() -> None:
    rt = OpenAIRuntime("")
    with pytest.raises(ValueError, match="missing"):
        rt.generate([("user", "hi")], max_new_tokens=100, temperature=0.7, top_k=20)


def test_generate_passes_screen_timeout_and_stops_when_cancelled(monkeypatch) -> None:
    cancelled = {"value": False}

    class CancellingStream(FakeStreamResponse):
        def __iter__(self):
            yield b'data: {"choices":[{"delta":{"content":"Chao"}}]}\n'
            cancelled["value"] = True
            yield b'data: {"choices":[{"delta":{"content":" anh"}}]}\n'

    seen: dict[str, float] = {}

    def fake_open(_request, timeout):
        seen["timeout"] = timeout
        return CancellingStream([])

    monkeypatch.setattr(runtime_module, "urlopen", fake_open)

    with pytest.raises(OpenAIGenerationCancelled):
        OpenAIRuntime("key").generate(
            [("user", "event")],
            64,
            0.8,
            40,
            timeout_seconds=4.0,
            max_retries=0,
            is_cancelled=lambda: cancelled["value"],
        )

    assert seen["timeout"] == 4.0


def test_generate_can_override_model_for_a_latency_sensitive_request(monkeypatch) -> None:
    captured: dict[str, object] = {}

    def fake_open(request, timeout):
        captured.update(json.loads(request.data.decode("utf-8")))
        return FakeStreamResponse([{"choices": [{"delta": {"content": "Chao anh"}}]}])

    monkeypatch.setattr(runtime_module, "urlopen", fake_open)

    result = OpenAIRuntime("key", model_name="kilo-auto/free").generate(
        [("user", "event")],
        64,
        0.8,
        40,
        model_override="poolside/laguna-s-2.1:free",
        reasoning_effort_override="none",
        max_retries=0,
    )

    assert result == "Chao anh"
    assert captured["model"] == "poolside/laguna-s-2.1:free"
    assert captured["reasoning"] == {"effort": "none"}


def test_stream_enforces_total_deadline_even_when_chunks_keep_arriving(monkeypatch) -> None:
    stream = FakeStreamResponse(
        [
            {"choices": [{"delta": {"reasoning": "still thinking"}}]},
            {"choices": [{"delta": {"content": "too late"}}]},
        ]
    )
    ticks = iter((0.0, 1.0, 5.0))
    monkeypatch.setattr(runtime_module, "perf_counter", lambda: next(ticks))
    monkeypatch.setattr(runtime_module, "urlopen", lambda _request, timeout: stream)

    with pytest.raises(RuntimeError, match="deadline"):
        OpenAIRuntime("key")._read_stream(
            lambda: Request("https://example.test/stream"),
            None,
            timeout_seconds=4.0,
        )
