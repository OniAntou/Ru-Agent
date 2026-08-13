import threading
from pathlib import Path
from types import SimpleNamespace

from ru_runtime.backend_core import MiniGPTCore
from ru_runtime.headless_runner import BACKEND_KILO


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


def test_clear_memory_waits_for_an_inflight_remember_write(tmp_path: Path) -> None:
    core = MiniGPTCore()
    core.memory_index_path = tmp_path / "memory.faiss"
    core.memory_texts_path = tmp_path / "memory_texts.json"
    core.memory_db_path = tmp_path / "memory.db"
    remember_started = threading.Event()
    release_remember = threading.Event()
    clear_started = threading.Event()
    clear_finished = threading.Event()

    class BlockingStore:
        closed = False

        def remember_turn(self, prompt: str, reply: str) -> None:
            remember_started.set()
            assert release_remember.wait(timeout=2)

        def close(self) -> None:
            self.closed = True

    store = BlockingStore()
    core.memory_store = store
    remember_thread = threading.Thread(
        target=lambda: core.remember("prompt", "reply"),
    )

    def clear() -> None:
        clear_started.set()
        core.clear_memory()
        clear_finished.set()

    clear_thread = threading.Thread(target=clear)
    remember_thread.start()
    try:
        assert remember_started.wait(timeout=1)
        clear_thread.start()
        assert clear_started.wait(timeout=1)
        assert not clear_finished.wait(timeout=0.1)
    finally:
        release_remember.set()
        remember_thread.join(timeout=1)
        clear_thread.join(timeout=1)

    assert clear_finished.is_set()
    assert store.closed is True
    assert core.memory_store is None


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


def test_cloud_backend_reads_memory_by_default(monkeypatch) -> None:
    core = MiniGPTCore()
    core.runner.backend = BACKEND_KILO

    class Store:
        def retrieve(self, prompt):
            return ["remembered"]

        def render_context(self, records):
            assert records == ["remembered"]
            return "memory context"

    captured = {}
    monkeypatch.setattr(core, "get_memory_store", lambda: Store())
    monkeypatch.setattr(
        core.runner,
        "generate_reply",
        lambda **kwargs: captured.update(kwargs) or "ok",
    )

    assert core.generate_reply_with_memory("hello") == "ok"
    assert captured["memory_context"] == "memory context"


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


def test_list_memory_records_uses_the_locked_store(monkeypatch) -> None:
    core = MiniGPTCore()
    store = SimpleNamespace(list_active=lambda: ["record"])
    monkeypatch.setattr(core, "get_memory_store", lambda: store)

    assert core.list_memory_records() == ["record"]
