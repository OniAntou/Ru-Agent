from pathlib import Path

import pytest

import mini_gpt.headless_runner as runtime


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
