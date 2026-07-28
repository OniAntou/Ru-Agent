import json

from mini_gpt.openai_runtime import OpenAIRuntime


def test_generate_reports_one_complete_request_duration(monkeypatch) -> None:
    class Response:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def read(self):
            return b'{"choices":[{"message":{"content":"Ru"}}]}'

        def __iter__(self):
            return iter(
                (
                    b'data: {"choices":[{"delta":{"content":"Ru"}}]}\n',
                    b'data: [DONE]\n',
                )
            )

    ticks = iter((10.0, 12.5))
    monkeypatch.setattr("mini_gpt.openai_runtime.urlopen", lambda *_args, **_kwargs: Response())
    monkeypatch.setattr("mini_gpt.openai_runtime.perf_counter", ticks.__next__)
    timings: list[float] = []

    runtime = OpenAIRuntime("key", "https://opencode.ai/zen/v1", "deepseek-v4-flash")
    assert runtime.generate(
        [("user", "Xin chao")], 32, 0.7, 20, on_complete_timing=timings.append
    ) == "Ru"
    assert timings == [2.5]


def test_generate_streams_complete_snapshots_and_returns_the_complete_reply(monkeypatch) -> None:
    class Response:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def __iter__(self):
            return iter(
                (
                    b'data: {"choices":[{"delta":{"content":"Em "}}]}\n',
                    b'data: {"choices":[{"delta":{"content":"chao anh"}}]}\n',
                    b'data: [DONE]\n',
                )
            )

    captured_requests = []
    monkeypatch.setattr(
        "mini_gpt.openai_runtime.urlopen",
        lambda request, **_kwargs: captured_requests.append(request) or Response(),
    )
    deltas: list[str] = []

    result = OpenAIRuntime("key").generate(
        [("user", "Xin chao")], 32, 0.7, 20, on_delta=deltas.append
    )

    assert result == "Em chao anh"
    assert deltas == ["Em ", "Em chao anh"]
    assert json.loads(captured_requests[0].data)["stream"] is True


def test_generate_converts_cumulative_stream_snapshots_to_new_suffixes(monkeypatch) -> None:
    class Response:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def __iter__(self):
            return iter(
                (
                    b'data: {"choices":[{"delta":{"content":"Ch"}}]}\n',
                    b'data: {"choices":[{"delta":{"content":"Chao anh"}}]}\n',
                    b'data: {"choices":[{"delta":{"content":"Chao anh! Em vui qua."}}]}\n',
                    b'data: [DONE]\n',
                )
            )

    monkeypatch.setattr("mini_gpt.openai_runtime.urlopen", lambda *_args, **_kwargs: Response())
    deltas: list[str] = []

    result = OpenAIRuntime("key").generate(
        [("user", "Xin chao")], 32, 0.7, 20, on_delta=deltas.append
    )

    assert result == "Chao anh! Em vui qua."
    assert deltas == ["Ch", "Chao anh", "Chao anh! Em vui qua."]


def test_generate_accepts_a_completed_message_content_event(monkeypatch) -> None:
    class Response:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def __iter__(self):
            return iter(
                (
                    b'data: {"choices":[{"message":{"content":"Ru da tra loi"}}]}\n',
                    b'data: [DONE]\n',
                )
            )

    monkeypatch.setattr("mini_gpt.openai_runtime.urlopen", lambda *_args, **_kwargs: Response())

    assert OpenAIRuntime("key").generate([("user", "Xin chao")], 32, 0.7, 20) == "Ru da tra loi"


def test_generate_retries_one_empty_stream_before_reporting_failure(monkeypatch) -> None:
    class EmptyResponse:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def __iter__(self):
            return iter((b'data: {"choices":[{"delta":{"role":"assistant"}}]}\n', b'data: [DONE]\n'))

    class CompleteResponse:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def __iter__(self):
            return iter((b'data: {"choices":[{"delta":{"content":"Da thu lai xong."}}]}\n', b'data: [DONE]\n'))

    responses = iter((EmptyResponse(), CompleteResponse()))
    requests = []
    monkeypatch.setattr(
        "mini_gpt.openai_runtime.urlopen",
        lambda request, **_kwargs: requests.append(request) or next(responses),
    )

    assert OpenAIRuntime("key").generate([("user", "Thu lai")], 32, 0.7, 20) == "Da thu lai xong."
    assert len(requests) == 2
