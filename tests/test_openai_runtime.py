from __future__ import annotations

import contextlib
import json

import httpx
import pytest

import ru_runtime.generation.openai as runtime_module
from ru_runtime.generation.openai import OpenAIGenerationCancelled, OpenAIRuntime
from ru_runtime.policy.text import append_stream_text


class FakeStreamResponse:
    def __init__(self, events: list[dict[str, object]]) -> None:
        self._lines = [
            f"data: {json.dumps(event)}\n" for event in events
        ] + ["data: [DONE]\n"]
        self.status_code = 200
        self.reason_phrase = "OK"
        self.headers: dict[str, str] = {}

    def __enter__(self) -> FakeStreamResponse:
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
            with contextlib.suppress(json.JSONDecodeError, TypeError):
                capture.update(json.loads(content))
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


def test_stream_callback_receives_incremental_provider_events(monkeypatch) -> None:
    stream = FakeStreamResponse(
        [
            {"choices": [{"delta": {"content": "Ch"}}]},
            {"choices": [{"delta": {"content": "ào"}}]},
            {"choices": [{"delta": {"content": " anh"}}]},
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
            with contextlib.suppress(json.JSONDecodeError, TypeError):
                captured.update(json.loads(content))
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


def test_fetch_models_allows_local_servers_without_api_key(monkeypatch) -> None:
    captured: dict[str, object] = {}

    def fake_get(self, url, *, headers=None, **kwargs):
        captured["url"] = str(url)
        captured["headers"] = headers
        return httpx.Response(
            200,
            json={"data": [{"id": "qwen2.5:latest"}, {"id": "llama3.2:latest"}]},
            request=httpx.Request("GET", str(url)),
        )

    monkeypatch.setattr(httpx.Client, "get", fake_get)

    models = OpenAIRuntime.fetch_models("", "http://localhost:11434/v1")
    assert [m["id"] for m in models] == ["llama3.2:latest", "qwen2.5:latest"]
    assert captured["url"] == "http://localhost:11434/v1/models"


def test_fetch_models_raises_for_cloud_when_key_is_blank() -> None:
    with pytest.raises(ValueError, match="API Key is missing"):
        OpenAIRuntime.fetch_models("", "https://opencode.ai/zen/v1")


@pytest.mark.parametrize(
    "base_url",
    [
        "https://attacker.example/api/1234",
        "https://localhost.attacker.example/v1",
        "https://example.test/11434/v1",
        "http://192.168.1.50:1234/v1",
    ],
)
def test_generate_rejects_remote_urls_that_contain_local_tokens(monkeypatch, base_url: str) -> None:
    runtime = OpenAIRuntime("", base_url=base_url)
    monkeypatch.setattr(runtime, "_read_stream", lambda *args, **kwargs: "unexpected")

    with pytest.raises(ValueError, match="missing"):
        runtime.generate(
            [("user", "hello")],
            max_new_tokens=32,
            temperature=0.7,
            top_k=20,
            max_retries=0,
        )


@pytest.mark.parametrize(
    "base_url",
    [
        "http://localhost:11434/v1",
        "http://127.0.0.1:1234/v1",
        "http://[::1]:8000/v1",
    ],
)
def test_generate_allows_exact_loopback_urls_without_api_key(monkeypatch, base_url: str) -> None:
    runtime = OpenAIRuntime("", base_url=base_url)
    monkeypatch.setattr(runtime, "_read_stream", lambda *args, **kwargs: "local response")

    assert runtime.generate(
        [("user", "hello")],
        max_new_tokens=32,
        temperature=0.7,
        top_k=20,
        max_retries=0,
    ) == "local response"


def test_model_runner_supports_ollama_and_lmstudio_backends() -> None:
    from ru_runtime.generation.runner import (
        BACKEND_CUSTOM,
        BACKEND_LMSTUDIO,
        BACKEND_OLLAMA,
        ModelRunner,
    )

    runner = ModelRunner()

    runner.select_backend(BACKEND_OLLAMA)
    status = runner.load(model_name="qwen2.5:latest")
    assert "Ollama API" in status
    assert runner.openai_runtime.base_url == "http://localhost:11434/v1"
    assert runner.openai_runtime.model_name == "qwen2.5:latest"

    runner.select_backend(BACKEND_LMSTUDIO)
    status = runner.load(model_name="mistral-7b")
    assert "LM Studio API" in status
    assert runner.openai_runtime.base_url == "http://localhost:1234/v1"
    assert runner.openai_runtime.model_name == "mistral-7b"

    runner.select_backend(BACKEND_CUSTOM)
    status = runner.load(model_name="custom-v1", base_url="http://192.168.1.50:8000/v1")
    assert "Custom OpenAI API" in status
    assert runner.openai_runtime.base_url == "http://192.168.1.50:8000/v1"
    assert runner.openai_runtime.model_name == "custom-v1"


def test_status_get_models_handles_ollama_and_lmstudio(monkeypatch) -> None:
    from ru_runtime.worker.v1.status import get_models

    def fake_get(self, url, *, headers=None, **kwargs):
        return httpx.Response(
            200,
            json={"data": [{"id": "local-model-1"}]},
            request=httpx.Request("GET", str(url)),
        )

    monkeypatch.setattr(httpx.Client, "get", fake_get)

    result_ollama = get_models("ollama")
    assert result_ollama["status"] == "ok"
    assert result_ollama["models"] == ["local-model-1"]

    result_lmstudio = get_models("lmstudio")
    assert result_lmstudio["status"] == "ok"
    assert result_lmstudio["models"] == ["local-model-1"]

