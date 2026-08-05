from pathlib import Path

import pytest

from mini_gpt.llama_cpp_runtime import LlamaCppRuntime, fit_messages_to_context


@pytest.fixture
def runtime(tmp_path: Path) -> LlamaCppRuntime:
    return LlamaCppRuntime(project_root=tmp_path)


def test_server_command_keeps_model_after_lora(runtime: LlamaCppRuntime, tmp_path: Path) -> None:
    lora = tmp_path / "models" / "gguf" / "ru-llama3.1-8b-instruct-v6.2-lora.gguf"
    lora.parent.mkdir(parents=True)
    lora.write_bytes(b"x" * 1024)
    base = tmp_path / "models" / "gguf" / "llama3.1-8b-instruct-q4_k_m.gguf"
    base.write_bytes(b"x" * 1024)

    command = runtime.server_command("all")

    assert "--model" in command
    model_index = command.index("--model")
    lora_index = command.index("--lora")
    assert command[model_index + 1] == str(base)
    assert command[lora_index + 1] == str(lora)
    assert command.index("--host") > lora_index


def test_server_command_without_lora_has_no_flag(runtime: LlamaCppRuntime) -> None:
    command = runtime.server_command("0")
    assert "--lora" not in command
    assert command[command.index("--model") + 1] == str(runtime.base_model)


def test_server_command_includes_loopback_auth_key(runtime: LlamaCppRuntime) -> None:
    command = runtime.server_command("0")

    assert "--api-key" in command
    key = command[command.index("--api-key") + 1]
    assert len(key) == 64
    assert key == runtime.api_key


def test_request_headers_carry_bearer_token(runtime: LlamaCppRuntime) -> None:
    headers = runtime._request_headers({"messages": []})

    assert headers["Authorization"] == f"Bearer {runtime.api_key}"
    assert headers["Content-Type"] == "application/json"

    headers = runtime._request_headers(None)
    assert headers["Authorization"] == f"Bearer {runtime.api_key}"
    assert "Content-Type" not in headers


def test_server_command_validates_assets_with_missing_lora(runtime: LlamaCppRuntime) -> None:
    with pytest.raises(FileNotFoundError, match="Ru LoRA GGUF"):
        runtime.validate_assets()


def test_generate_sends_strong_repetition_guard() -> None:
    captured: dict = {}

    class FakeProcess:
        def poll(self) -> None:
            return None

        def terminate(self) -> None:
            pass

        def wait(self, timeout: float | None = None) -> int:
            return 0

    def fake_request(url: str, body: dict | None, timeout: float) -> dict[str, object]:
        captured["body"] = body
        return {"choices": [{"message": {"content": "Xin chao"}}]}

    rt = LlamaCppRuntime(
        project_root=Path("."),
        request=fake_request,
        popen=lambda *args, **kwargs: FakeProcess(),
    )
    out = rt.generate(
        [("system", "rules"), ("user", "Xin chao")],
        max_new_tokens=32,
        temperature=0.7,
        top_k=50,
    )

    assert out == "Xin chao"
    assert captured["body"]["repeat_penalty"] == 1.4
    assert captured["body"]["repeat_last_n"] == 512


def test_context_fitting_preserves_contract_and_current_prompt_while_dropping_oldest_history() -> None:
    messages = (
        ("system", "rules " * 300),
        ("user", "old question " * 300),
        ("assistant", "old answer " * 300),
        ("user", "current prompt " * 100),
    )

    fitted = fit_messages_to_context(messages, max_new_tokens=1024)

    assert fitted[0][0] == "system"
    assert fitted[-1][0] == "user"
    assert "current prompt" in fitted[-1][1]
    assert all("old question" not in content for _, content in fitted)
    assert sum(len(content) for _, content in fitted) <= (2048 - 1024 - 64) * 4
