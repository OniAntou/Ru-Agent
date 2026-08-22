from __future__ import annotations

from types import SimpleNamespace

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


def test_generate_reply_accepts_structured_tools_only_for_agent_turns(monkeypatch) -> None:
    captured: dict[str, object] = {}

    def fake_execute_reply(_generate, **kwargs):
        captured.update(kwargs)
        return "reply"

    monkeypatch.setattr(runner_module, "execute_reply", fake_execute_reply)

    reply = ModelRunner().generate_reply(
        "ping",
        128,
        0.7,
        40,
        structured_tools_only=True,
        action_capabilities=(
            SimpleNamespace(tool_id="system.volume", tool_version="1.0.0"),
        ),
    )

    assert reply == "reply"
    assert captured["allow_legacy_markers"] is False
    assert "system.volume@1.0.0" in str(captured["system_instruction"])
    assert "[VOLUME: up]" not in str(captured["system_instruction"])
    assert "[VOLUME: set 2]" not in str(captured["system_instruction"])


def test_empty_structured_capabilities_never_advertise_volume(monkeypatch) -> None:
    captured: dict[str, object] = {}

    def fake_execute_reply(_generate, **kwargs):
        captured.update(kwargs)
        return "reply"

    monkeypatch.setattr(runner_module, "execute_reply", fake_execute_reply)

    ModelRunner().generate_reply(
        "ping",
        128,
        0.7,
        40,
        structured_tools_only=True,
        action_capabilities=(),
    )

    assert "system.volume@1.0.0" not in str(captured["system_instruction"])


def test_generate_reply_has_a_separate_agent_follow_up_phase(monkeypatch) -> None:
    captured: dict[str, object] = {}

    def fake_execute_reply(_generate, **kwargs):
        captured.update(kwargs)
        return "reply"

    monkeypatch.setattr(runner_module, "execute_reply", fake_execute_reply)

    reply = ModelRunner().generate_reply(
        "ping",
        128,
        0.7,
        40,
        agent_follow_up=True,
    )

    assert reply == "reply"
    assert captured["allow_legacy_markers"] is False
    assert captured["agent_follow_up"] is True
    assert "[VOLUME: up]" not in str(captured["system_instruction"])
    assert "[VOLUME: set 2]" not in str(captured["system_instruction"])


@pytest.mark.parametrize("phase", ("normal", "follow_up"))
def test_non_selection_prompts_do_not_advertise_structured_capabilities(
    monkeypatch,
    phase: str,
) -> None:
    captured: dict[str, object] = {}

    def fake_execute_reply(_generate, **kwargs):
        captured.update(kwargs)
        return "reply"

    monkeypatch.setattr(runner_module, "execute_reply", fake_execute_reply)

    ModelRunner().generate_reply(
        "ping",
        128,
        0.7,
        40,
        agent_follow_up=phase == "follow_up",
        action_capabilities=(
            SimpleNamespace(tool_id="system.volume", tool_version="1.0.0"),
        ),
    )

    assert "system.volume@1.0.0" not in str(captured["system_instruction"])
