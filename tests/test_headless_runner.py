from pathlib import Path
from types import SimpleNamespace

import pytest

import mini_gpt.headless_runner as runtime
from mini_gpt.ru_style import RU_SCREEN_REACTION_SYSTEM_PROMPT


def test_transformers_load_reports_missing_adapter_instead_of_name_error(
    monkeypatch, tmp_path: Path
) -> None:
    runner = runtime.ModelRunner()
    runner.select_backend(runtime.BACKEND_TRANSFORMERS)
    monkeypatch.setattr(runtime, "dependency_status", lambda: ())

    with pytest.raises(FileNotFoundError, match="adapter directory not found"):
        runner.load(tmp_path / "missing-adapter")


def test_transformers_release_calls_runtime_cache_cleanup(monkeypatch) -> None:
    released: list[str] = []
    runner = runtime.ModelRunner()
    monkeypatch.setattr(runtime, "clear_llama_generation_cache", lambda: released.append("hf"))
    monkeypatch.setattr(runner.gguf_runtime, "stop", lambda: released.append("gguf"))

    runner.release_gpu()

    assert released == ["hf", "gguf"]


def test_screen_reaction_uses_hosted_runtime_without_search_memory_or_history() -> None:
    runner = runtime.ModelRunner()
    runner.backend = runtime.BACKEND_OPENCODE
    captured: dict[str, object] = {}
    runner.openai_runtime = SimpleNamespace(
        generate=lambda messages, *args, **kwargs: captured.update(
            messages=messages, kwargs=kwargs
        )
        or "Đau mắt giùm anh luôn."
    )

    reply = runner.generate_screen_reaction(
        '{"event":"missed shots"}',
        vts_instruction="",
        is_cancelled=lambda: False,
    )

    assert reply == "Đau mắt giùm anh luôn."
    assert captured["messages"][0][1].startswith(RU_SCREEN_REACTION_SYSTEM_PROMPT)
    assert captured["messages"][1] == ("user", '{"event":"missed shots"}')
    assert captured["kwargs"]["timeout_seconds"] == 4.0
    assert captured["kwargs"]["max_retries"] == 0
