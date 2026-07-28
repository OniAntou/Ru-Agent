from mini_gpt.openai_runtime import OpenAIRuntime


def test_generate_reports_one_complete_request_duration(monkeypatch) -> None:
    class Response:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def read(self):
            return b'{"choices":[{"message":{"content":"Ru"}}]}'

    ticks = iter((10.0, 12.5))
    monkeypatch.setattr("mini_gpt.openai_runtime.urlopen", lambda *_args, **_kwargs: Response())
    monkeypatch.setattr("mini_gpt.openai_runtime.perf_counter", ticks.__next__)
    timings: list[float] = []

    runtime = OpenAIRuntime("key", "https://opencode.ai/zen/v1", "deepseek-v4-flash")
    assert runtime.generate(
        [("user", "Xin chao")], 32, 0.7, 20, on_complete_timing=timings.append
    ) == "Ru"
    assert timings == [2.5]
