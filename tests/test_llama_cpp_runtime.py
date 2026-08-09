import socket
from pathlib import Path

import pytest

from mini_gpt.llama_cpp_runtime import LlamaCppRuntime, fit_messages_to_context


@pytest.fixture
def runtime(tmp_path: Path) -> LlamaCppRuntime:
    return LlamaCppRuntime(project_root=tmp_path)


def test_server_command_uses_lfm25_without_a_legacy_lora(runtime: LlamaCppRuntime, tmp_path: Path) -> None:
    base = tmp_path / "models" / "gguf" / "LFM2.5-2.6B-Q4_K_M.gguf"
    base.parent.mkdir(parents=True)
    base.write_bytes(b"x" * 1024)

    command = runtime.server_command("all")

    assert "--model" in command
    model_index = command.index("--model")
    assert command[model_index + 1] == str(base)
    assert "--lora" not in command
    assert command.index("--host") > model_index


def test_server_command_without_lora_has_no_flag(runtime: LlamaCppRuntime) -> None:
    command = runtime.server_command("0")
    assert "--lora" not in command
    assert "--skip-chat-parsing" not in command
    assert "--jinja" in command
    assert command[command.index("--reasoning") + 1] == "off"
    assert command[command.index("--reasoning-budget") + 1] == "0"
    assert command[command.index("--model") + 1] == str(runtime.base_model)


def test_validate_assets_accepts_lfm25_base_without_a_lora(runtime: LlamaCppRuntime, tmp_path: Path) -> None:
    runtime.server_executable.parent.mkdir(parents=True)
    runtime.server_executable.write_bytes(b"x")
    runtime.base_model.parent.mkdir(parents=True)
    runtime.base_model.write_bytes(b"x")

    runtime.validate_assets()


def test_server_command_includes_loopback_auth_key(runtime: LlamaCppRuntime) -> None:
    command = runtime.server_command("0")

    assert "--api-key" in command
    key = command[command.index("--api-key") + 1]
    assert len(key) == 64
    assert key == runtime.api_key


@pytest.mark.parametrize("listening", [False, True])
def test_ensure_started_refuses_an_occupied_port_without_spawning(
    runtime: LlamaCppRuntime, monkeypatch, listening: bool
) -> None:
    launches: list[tuple[str, ...]] = []

    class FakeProcess:
        pid = 1234
        returncode = None

        def poll(self):
            return None

        def terminate(self) -> None:
            pass

        def wait(self, timeout: float | None = None) -> int:
            return 0

    def fake_popen(command, **kwargs):
        launches.append(tuple(command))
        return FakeProcess()

    runtime._popen = fake_popen
    runtime.start_timeout_seconds = 0
    monkeypatch.setattr(runtime, "validate_assets", lambda: None)

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        if listening:
            listener.listen()
        runtime.port = listener.getsockname()[1]

        with pytest.raises(RuntimeError, match="already in use"):
            runtime.ensure_started()

    assert launches == []


def test_start_detects_a_foreign_server_after_spawn_without_waiting_for_timeout(
    runtime: LlamaCppRuntime, monkeypatch
) -> None:
    requested_endpoints: list[str] = []

    class FakeProcess:
        pid = 1234
        returncode: int | None = None
        terminated = False

        def poll(self) -> int | None:
            return self.returncode

        def terminate(self) -> None:
            self.terminated = True
            self.returncode = 0

        def wait(self, timeout: float | None = None) -> int:
            return 0

    process = FakeProcess()

    def foreign_server(endpoint: str, _payload: dict | None, _timeout: float) -> dict:
        requested_endpoints.append(endpoint)
        if endpoint == "/v1/chat/completions":
            raise RuntimeError("llama.cpp HTTP 401: unauthorized")
        return {}

    runtime._request_impl = foreign_server
    runtime._popen = lambda *args, **kwargs: process
    monkeypatch.setattr(runtime, "_loopback_port_is_occupied", lambda: False)

    with pytest.raises(RuntimeError, match="already in use"):
        runtime._start_server("all")

    assert requested_endpoints == ["/v1/chat/completions"]
    assert process.terminated is True


def test_start_requires_the_protected_endpoint_for_ownership(
    runtime: LlamaCppRuntime, monkeypatch
) -> None:
    requested_endpoints: list[str] = []

    class FakeProcess:
        pid = 1234
        returncode: int | None = None

        def poll(self) -> int | None:
            return self.returncode

        def terminate(self) -> None:
            self.returncode = 0

        def wait(self, timeout: float | None = None) -> int:
            return 0

    def owned_server(endpoint: str, _payload: dict | None, _timeout: float) -> dict:
        requested_endpoints.append(endpoint)
        return {}

    runtime._request_impl = owned_server
    runtime._popen = lambda *args, **kwargs: FakeProcess()
    monkeypatch.setattr(runtime, "_loopback_port_is_occupied", lambda: False)
    monkeypatch.setattr(runtime, "_server_rejects_wrong_bearer", lambda: True)

    runtime._start_server("all")

    assert requested_endpoints == ["/v1/chat/completions"]
    runtime.stop()


def test_start_rejects_a_listener_that_accepts_the_wrong_bearer_token(
    runtime: LlamaCppRuntime, monkeypatch
) -> None:
    requested_endpoints: list[str] = []

    class FakeProcess:
        pid = 1234
        returncode: int | None = None
        terminated = False

        def poll(self) -> int | None:
            return self.returncode

        def terminate(self) -> None:
            self.terminated = True
            self.returncode = 0

        def wait(self, timeout: float | None = None) -> int:
            return 0

    process = FakeProcess()

    def permissive_listener(endpoint: str, _payload: dict | None, _timeout: float) -> dict:
        requested_endpoints.append(endpoint)
        return {"data": []}

    runtime._request_impl = permissive_listener
    runtime._popen = lambda *args, **kwargs: process
    monkeypatch.setattr(runtime, "_loopback_port_is_occupied", lambda: False)
    monkeypatch.setattr(runtime, "_server_rejects_wrong_bearer", lambda: False, raising=False)

    with pytest.raises(RuntimeError, match="already in use"):
        runtime._start_server("all")

    assert requested_endpoints == ["/v1/chat/completions"]
    assert process.terminated is True


def test_request_headers_carry_bearer_token(runtime: LlamaCppRuntime) -> None:
    headers = runtime._request_headers({"messages": []})

    assert headers["Authorization"] == f"Bearer {runtime.api_key}"
    assert headers["Content-Type"] == "application/json"

    headers = runtime._request_headers(None)
    assert headers["Authorization"] == f"Bearer {runtime.api_key}"
    assert "Content-Type" not in headers


def test_ownership_probe_requires_rejection_of_a_different_bearer_token(
    runtime: LlamaCppRuntime, monkeypatch
) -> None:
    attempted_keys: list[str | None] = []

    def reject_wrong_key(
        endpoint: str,
        payload: dict | None,
        timeout: float,
        *,
        api_key: str | None = None,
    ) -> dict:
        assert endpoint == "/v1/chat/completions"
        assert payload == {"messages": [], "max_tokens": 0, "stream": False}
        assert timeout > 0
        attempted_keys.append(api_key)
        raise RuntimeError("llama.cpp HTTP 401: unauthorized")

    monkeypatch.setattr(runtime, "_request_json", reject_wrong_key)

    assert runtime._server_rejects_wrong_bearer() is True
    assert attempted_keys[0] is not None
    assert attempted_keys[0] != runtime.api_key


def test_ownership_probe_retries_while_llama_server_is_loading(
    runtime: LlamaCppRuntime, monkeypatch
) -> None:
    def loading_server(*args, **kwargs):
        raise RuntimeError('llama.cpp HTTP 503: {"error":{"message":"Loading model"}}')

    monkeypatch.setattr(runtime, "_request_json", loading_server)

    with pytest.raises(RuntimeError, match="HTTP 503"):
        runtime._server_rejects_wrong_bearer()


def test_server_command_validates_assets_with_missing_llama_server(runtime: LlamaCppRuntime) -> None:
    with pytest.raises(FileNotFoundError, match="llama-server"):
        runtime.validate_assets()


def test_generate_sends_strong_repetition_guard(monkeypatch) -> None:
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
    # This injected in-memory request function represents the already-owned
    # fake server; real startup ownership is covered by dedicated tests.
    monkeypatch.setattr(rt, "validate_assets", lambda: None)
    monkeypatch.setattr(rt, "_server_rejects_wrong_bearer", lambda: True)
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
