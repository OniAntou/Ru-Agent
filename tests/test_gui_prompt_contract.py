from mini_gpt import gui

import pytest


def test_model_runner_uses_cloud_personality_for_opencode(monkeypatch) -> None:
    captured: dict[str, object] = {}

    def fake_execute(generate, **kwargs):
        captured.update(kwargs)
        return "ok"

    monkeypatch.setattr(gui, "execute_reply", fake_execute)
    runner = object.__new__(gui.ModelRunner)
    runner.backend = gui.BACKEND_OPENCODE

    assert runner.generate_reply("hello", 32, 0.7, 20) == "ok"
    instruction = str(captured["system_instruction"])
    assert instruction.startswith(gui.RU_CLOUD_SYSTEM_PROMPT)
    assert "authoritative Ru personality contract" in instruction


def test_model_runner_keeps_shared_personality_for_local_backends(monkeypatch) -> None:
    captured: dict[str, object] = {}

    def fake_execute(generate, **kwargs):
        captured.update(kwargs)
        return "ok"

    monkeypatch.setattr(gui, "execute_reply", fake_execute)
    runner = object.__new__(gui.ModelRunner)
    runner.backend = gui.BACKEND_GGUF

    runner.generate_reply("hello", 32, 0.7, 20)
    instruction = str(captured["system_instruction"])
    assert instruction.startswith(gui.RU_SYSTEM_PROMPT)
    assert "authoritative Ru personality contract" not in instruction


def test_model_runner_rejects_an_unsupported_backend() -> None:
    runner = object.__new__(gui.ModelRunner)
    runner.backend = gui.BACKEND_GGUF

    with pytest.raises(ValueError, match="unsupported backend"):
        runner.select_backend("not-a-backend")
