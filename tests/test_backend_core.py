from pathlib import Path

from mini_gpt.backend_core import MiniGPTCore
from mini_gpt.headless_runner import BACKEND_KILO


def test_clear_memory_closes_sqlite_before_removing_database(tmp_path: Path) -> None:
    core = MiniGPTCore()
    core.memory_index_path = tmp_path / "memory.faiss"
    core.memory_texts_path = tmp_path / "memory_texts.json"
    core.memory_db_path = tmp_path / "memory.db"
    store = core.get_memory_store()
    store.remember_turn("Anh thích trả lời ngắn.", "Em sẽ trả lời ngắn.")

    core.clear_memory()

    assert core.memory_store is None
    assert not core.memory_db_path.exists()


def test_cloud_backend_does_not_receive_saved_memory_without_consent(monkeypatch) -> None:
    core = MiniGPTCore()
    core.runner.backend = BACKEND_KILO

    class Store:
        def retrieve(self, prompt):
            raise AssertionError("private memory must not be read for cloud generation")

    captured = {}
    monkeypatch.setattr(core, "get_memory_store", lambda: Store())
    monkeypatch.setattr(
        core.runner,
        "generate_reply",
        lambda **kwargs: captured.update(kwargs) or "ok",
    )

    assert core.generate_reply_with_memory("hello", share_memory_with_cloud=False) == "ok"
    assert captured["memory_context"] is None


def test_generation_forwards_stream_reset_and_vts_instruction(monkeypatch) -> None:
    core = MiniGPTCore()
    captured = {}
    monkeypatch.setattr(
        core.runner,
        "generate_reply",
        lambda **kwargs: captured.update(kwargs) or "ok",
    )
    reset = lambda: None

    core.generate_reply_with_memory(
        "hello", on_stream_reset=reset, vts_instruction="private catalog"
    )

    assert captured["on_stream_reset"] is reset
    assert captured["vts_instruction"] == "private catalog"
