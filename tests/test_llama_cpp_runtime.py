from __future__ import annotations

import pytest

from mini_gpt.llama_cpp_runtime import LlamaCppRuntime


def test_server_command_loads_the_base_and_v6_lora(tmp_path):
    runtime = LlamaCppRuntime(project_root=tmp_path)
    command = runtime.server_command()

    assert "--model" in command
    assert "--lora" in command
    assert command[command.index("--ctx-size") + 1] == "2048"
    assert command[command.index("--n-gpu-layers") + 1] == "all"
    assert str(tmp_path / "models" / "gguf" / "ru-llama3.1-8b-instruct-v6-lora.gguf") in command


def test_chat_completion_parses_openai_response(tmp_path):
    runtime = LlamaCppRuntime(
        project_root=tmp_path,
        request=lambda *_args, **_kwargs: {"choices": [{"message": {"content": "Chào anh"}}]},
    )
    runtime.ensure_started = lambda: None

    assert runtime.generate((("system", "s"), ("user", "u")), 32, 0.7, 20) == "Chào anh"


def test_chat_completion_rejects_empty_server_response(tmp_path):
    runtime = LlamaCppRuntime(project_root=tmp_path, request=lambda *_args, **_kwargs: {"choices": []})
    runtime.ensure_started = lambda: None

    with pytest.raises(RuntimeError, match="empty chat completion"):
        runtime.generate((("system", "s"), ("user", "u")), 32, 0.7, 20)


def test_validate_assets_reports_missing_server_binary(tmp_path):
    runtime = LlamaCppRuntime(project_root=tmp_path)

    with pytest.raises(FileNotFoundError, match="llama-server"):
        runtime.validate_assets()
