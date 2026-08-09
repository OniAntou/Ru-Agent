from types import SimpleNamespace

import pytest

import mini_gpt.headless_runner as runtime
from mini_gpt.ru_style import RU_SCREEN_REACTION_SYSTEM_PROMPT


def test_select_backend_rejects_retired_transformers_backend() -> None:
    runner = runtime.ModelRunner()
    with pytest.raises(ValueError, match="unsupported backend"):
        runner.select_backend("Transformers")


def test_release_gpu_stops_gguf_runtime(monkeypatch) -> None:
    stopped: list[str] = []
    runner = runtime.ModelRunner()
    monkeypatch.setattr(runner.gguf_runtime, "stop", lambda: stopped.append("gguf"))

    runner.release_gpu()

    assert stopped == ["gguf"]


def test_kilo_chat_disables_reasoning_by_default() -> None:
    runner = runtime.ModelRunner()
    runner.backend = runtime.BACKEND_KILO
    captured: dict[str, object] = {}
    runner.openai_runtime = SimpleNamespace(
        generate=lambda _messages, *_args, **kwargs: captured.update(kwargs) or "reply"
    )

    runner._generate((("user", "Xin chào"),), 128, 0.7, 40)

    assert captured["reasoning_effort_override"] == "none"


def test_kilo_chat_keeps_reasoning_when_explicitly_enabled() -> None:
    runner = runtime.ModelRunner()
    runner.backend = runtime.BACKEND_KILO
    runner.kilo_reasoning_enabled = True
    captured: dict[str, object] = {}
    runner.openai_runtime = SimpleNamespace(
        generate=lambda _messages, *_args, **kwargs: captured.update(kwargs) or "reply"
    )

    runner._generate((("user", "Giải bài này"),), 128, 0.7, 40)

    assert "reasoning_effort_override" not in captured


def test_gguf_checkpoint_path_rejects_sibling_prefix(monkeypatch, tmp_path) -> None:
    runner = runtime.ModelRunner()
    runner.backend = runtime.BACKEND_GGUF
    monkeypatch.setattr(runner.gguf_runtime, "validate_assets", lambda: None)
    sibling = runtime.PROJECT_ROOT / "models-evil" / "checkpoint.gguf"

    with pytest.raises(ValueError, match="must be inside"):
        runner.load(sibling)


def test_gguf_load_defaults_to_lfm25_when_checkpoint_is_blank(monkeypatch) -> None:
    runner = runtime.ModelRunner()
    monkeypatch.setattr(runner.gguf_runtime, "validate_assets", lambda: None)

    status = runner.load("")

    assert runner.checkpoint_path == runner.gguf_runtime.base_model.resolve()
    assert "LFM2.5-2.6B Q4_K_M" in status


def test_gguf_load_migrates_an_allowed_legacy_checkpoint_to_lfm25(monkeypatch) -> None:
    runner = runtime.ModelRunner()
    monkeypatch.setattr(runner.gguf_runtime, "validate_assets", lambda: None)

    runner.load("checkpoints/ru-llama3.1-8b-instruct-v6.2-fresh")

    assert runner.checkpoint_path == runner.gguf_runtime.base_model.resolve()


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


def test_kilo_auto_free_uses_direct_fast_model_for_screen_reaction() -> None:
    runner = runtime.ModelRunner()
    runner.backend = runtime.BACKEND_KILO
    captured: dict[str, object] = {}
    runner.openai_runtime = SimpleNamespace(
        model_name="kilo-auto/free",
        generate=lambda messages, *args, **kwargs: captured.update(
            messages=messages, args=args, kwargs=kwargs
        )
        or "Em xin phep che mat mot giay.",
    )

    runner.generate_screen_reaction(
        '{"event":"missed shots"}',
        vts_instruction=" available: vts_01 = cover face",
        is_cancelled=lambda: False,
    )

    assert captured["kwargs"]["model_override"] == runtime.KILO_SCREEN_REACTION_MODEL
    assert runtime.KILO_SCREEN_REACTION_MODEL == "inclusionai/ling-3.0-flash:free"
    assert captured["kwargs"]["reasoning_effort_override"] == "none"
    assert captured["args"][0] == 256
    system = captured["messages"][0][1]
    assert len(system) < 3_000
    assert "available: vts_01 = cover face" in system


def test_explicit_kilo_model_is_not_overridden_for_screen_reaction() -> None:
    runner = runtime.ModelRunner()
    runner.backend = runtime.BACKEND_KILO
    captured: dict[str, object] = {}
    runner.openai_runtime = SimpleNamespace(
        model_name="anthropic/claude-haiku-4.5",
        generate=lambda _messages, *args, **kwargs: captured.update(kwargs=kwargs)
        or "Anh lai lam em dau mat roi.",
    )

    runner.generate_screen_reaction(
        '{"event":"missed shots"}',
        vts_instruction="",
        is_cancelled=lambda: False,
    )

    assert captured["kwargs"]["model_override"] is None
    assert captured["kwargs"]["reasoning_effort_override"] is None


def test_kilo_screen_reaction_falls_back_after_immediate_free_provider_error() -> None:
    runner = runtime.ModelRunner()
    runner.backend = runtime.BACKEND_KILO
    calls: list[dict[str, object]] = []

    def generate(_messages, *_args, **kwargs):
        calls.append(kwargs)
        if len(calls) == 1:
            raise RuntimeError("OpenCode API HTTP 429: Too Many Requests")
        return "Thoi anh, lai truot nua roi."

    runner.openai_runtime = SimpleNamespace(model_name="kilo-auto/free", generate=generate)

    reply = runner.generate_screen_reaction(
        '{"event":"missed shots"}',
        vts_instruction="",
        is_cancelled=lambda: False,
    )

    assert reply == "Thoi anh, lai truot nua roi."
    assert [call["model_override"] for call in calls] == [
        runtime.KILO_SCREEN_REACTION_MODEL,
        runtime.KILO_SCREEN_REACTION_FALLBACK_MODEL,
    ]


def test_kilo_screen_reaction_does_not_chain_after_deadline() -> None:
    runner = runtime.ModelRunner()
    runner.backend = runtime.BACKEND_KILO
    calls = 0

    def generate(_messages, *_args, **_kwargs):
        nonlocal calls
        calls += 1
        raise RuntimeError("Hosted generation exceeded its total response deadline")

    runner.openai_runtime = SimpleNamespace(model_name="kilo-auto/free", generate=generate)

    with pytest.raises(RuntimeError, match="deadline"):
        runner.generate_screen_reaction(
            '{"event":"missed shots"}',
            vts_instruction="",
            is_cancelled=lambda: False,
        )

    assert calls == 1
