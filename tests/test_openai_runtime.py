from __future__ import annotations

import json
from collections.abc import Callable

import httpx
import pytest

import ru_runtime.generation.openai as runtime_module
from ru_runtime.policy.text import append_stream_text
from ru_runtime.generation.openai import OpenAIGenerationCancelled, OpenAIRuntime


class FakeStreamResponse:
    def __init__(self, events: list[dict[str, object]]) -> None:
        self._lines = [
            f"data: {json.dumps(event)}\n" for event in events
        ] + ["data: [DONE]\n"]
        self.status_code = 200
        self.reason_phrase = "OK"
        self.headers: dict[str, str] = {}

    def __enter__(self) -> "FakeStreamResponse":
        return self

    def __exit__(self, *_args: object) -> None:
        return None

    def raise_for_status(self) -> None:
        pass

    def iter_lines(self) -> list[str]:
        return self._lines

    def read(self) -> bytes:
        return "".join(self._lines).encode("utf-8")


class EmptyStreamResponse(FakeStreamResponse):
    def __init__(self) -> None:
        super().__init__([])
        self._lines = ["data: [DONE]\n"]


class MalformedStreamResponse(FakeStreamResponse):
    def __init__(self) -> None:
        super().__init__([])
        self._lines = ['data: NOT_VALID_JSON\n', "data: [DONE]\n"]


class ErrorStreamResponse(FakeStreamResponse):
    def __init__(self) -> None:
        super().__init__([])
        self._lines = ['data: {"error": "rate limit exceeded"}\n', "data: [DONE]\n"]


class PartialNetworkFailureResponse(FakeStreamResponse):
    def __init__(self) -> None:
        super().__init__([{"choices": [{"delta": {"content": "partial"}}]}])

    def iter_lines(self):
        yield self._lines[0]
        raise httpx.ReadError("connection dropped", request=httpx.Request("POST", "https://test"))


def _mock_stream(
    monkeypatch,
    response: FakeStreamResponse,
    *,
    capture: dict[str, object] | None = None,
) -> None:
    def fake_stream(method, url, *, headers=None, content=None, **kwargs):
        if capture is not None and content is not None:
            try:
                capture.update(json.loads(content))
            except (json.JSONDecodeError, TypeError):
                pass
        if capture is not None:
            capture["timeout"] = kwargs.get("timeout")
        return response

    monkeypatch.setattr(httpx, "stream", fake_stream)


def test_stream_callback_receives_only_new_text_for_cumulative_provider_events(monkeypatch) -> None:
    stream = FakeStreamResponse(
        [
            {"choices": [{"delta": {"content": "Ch"}}]},
            {"choices": [{"delta": {"content": "Chào"}}]},
            {"choices": [{"delta": {"content": "Chào anh"}}]},
        ]
    )
    _mock_stream(monkeypatch, stream)
    deltas: list[str] = []

    response = OpenAIRuntime("key")._read_stream(
        "https://example.test/stream",
        {},
        b"",
        deltas.append,
    )

    assert response == "Chào anh"
    assert deltas == ["Ch", "ào", " anh"]
    visible = ""
    for delta in deltas:
        visible = append_stream_text(visible, delta)
    assert visible == response


def test_empty_stream_raises_runtime_error(monkeypatch) -> None:
    _mock_stream(monkeypatch, EmptyStreamResponse())
    rt = OpenAIRuntime("key")
    with pytest.raises(RuntimeError, match="empty chat completions"):
        rt.generate([("user", "hello")], max_new_tokens=100, temperature=0.7, top_k=20)


def test_malformed_json_raises_runtime_error(monkeypatch) -> None:
    _mock_stream(monkeypatch, MalformedStreamResponse())
    rt = OpenAIRuntime("key")
    with pytest.raises(RuntimeError, match="invalid JSON"):
        rt._read_stream("https://test", {}, b"", None)


def test_stream_error_from_api_raises_runtime_error(monkeypatch) -> None:
    _mock_stream(monkeypatch, ErrorStreamResponse())
    rt = OpenAIRuntime("key")
    with pytest.raises(RuntimeError, match="stream error"):
        rt._read_stream("https://test", {}, b"", None)


def test_retry_emits_delta_from_empty_first_attempt(monkeypatch) -> None:
    call_count = 0
    captured: dict[str, object] = {}

    def make_response():
        nonlocal call_count
        call_count += 1
        if call_count == 1:
            return EmptyStreamResponse()
        return FakeStreamResponse([{"choices": [{"delta": {"content": "Hello"}}]}])

    def fake_stream(method, url, *, headers=None, content=None, **kwargs):
        if content is not None:
            try:
                captured.update(json.loads(content))
            except (json.JSONDecodeError, TypeError):
                pass
        return make_response()

    monkeypatch.setattr(httpx, "stream", fake_stream)
    deltas: list[str] = []
    rt = OpenAIRuntime("key")
    result = rt.generate(
        [("user", "hi")],
        max_new_tokens=100,
        temperature=0.7,
        top_k=20,
        on_delta=deltas.append,
    )
    assert result == "Hello"
    assert deltas == ["Hello"]


def test_retry_resets_partial_stream_before_emitting_retry(monkeypatch) -> None:
    calls = 0

    def fake_stream(method, url, *, headers=None, content=None, **kwargs):
        nonlocal calls
        calls += 1
        if calls == 1:
            return PartialNetworkFailureResponse()
        return FakeStreamResponse([{"choices": [{"delta": {"content": "retry"}}]}])

    monkeypatch.setattr(httpx, "stream", fake_stream)
    monkeypatch.setattr(runtime_module.time, "sleep", lambda _seconds: None)
    deltas: list[str] = []
    resets: list[None] = []

    result = OpenAIRuntime("key").generate(
        [("user", "hi")],
        max_new_tokens=100,
        temperature=0.7,
        top_k=20,
        on_delta=deltas.append,
        on_reset=lambda: resets.append(None),
        max_retries=1,
    )

    assert result == "retry"
    assert deltas == ["partial", "retry"]
    assert resets == [None]


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
        def __init__(self) -> None:
            super().__init__([])
            self._lines = []

        def iter_lines(self):
            yield 'data: {"choices":[{"delta":{"content":"Chao"}}]}\n'
            cancelled["value"] = True
            yield 'data: {"choices":[{"delta":{"content":" anh"}}]}\n'

    seen: dict[str, float] = {}

    def fake_stream(method, url, *, headers=None, content=None, **kwargs):
        seen["timeout"] = kwargs.get("timeout")
        return CancellingStream()

    monkeypatch.setattr(httpx, "stream", fake_stream)

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

    def fake_stream(method, url, *, headers=None, content=None, **kwargs):
        if content is not None:
            captured.update(json.loads(content))
        return FakeStreamResponse([{"choices": [{"delta": {"content": "Chao anh"}}]}])

    monkeypatch.setattr(httpx, "stream", fake_stream)

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
    _mock_stream(monkeypatch, stream)

    with pytest.raises(RuntimeError, match="deadline"):
        OpenAIRuntime("key")._read_stream(
            "https://example.test/stream",
            {},
            b"",
            None,
            timeout_seconds=4.0,
        )
