from __future__ import annotations

import pytest

from ru_runtime.generation import runner as runner_module
from ru_runtime.generation.runner import ModelRunner


def test_load_records_load_error_and_re_raises(monkeypatch) -> None:
    def fail_openai(*_args, **_kwargs) -> None:
        raise RuntimeError("cannot reach server")

    monkeypatch.setattr(runner_module, "OpenAIRuntime", fail_openai)
    model = ModelRunner()
    model.select_backend("Ollama")

    with pytest.raises(RuntimeError, match="cannot reach server"):
        model.load(model_name="qwen2.5:latest")

    assert model.load_error == "cannot reach server"


def test_load_clears_load_error_on_success() -> None:
    model = ModelRunner()
    model.select_backend("Ollama")

    model.load(model_name="qwen2.5:latest")

    assert model.load_error is None
    assert model.openai_runtime is not None
    assert model.openai_runtime.model_name == "qwen2.5:latest"
