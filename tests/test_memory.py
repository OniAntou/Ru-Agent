import threading
from datetime import UTC, datetime, timedelta

import pytest

from ru_runtime.memory.store import MemoryKind, StructuredMemoryStore


@pytest.fixture(scope="session")
def shared_store(tmp_path_factory):
    base = tmp_path_factory.mktemp("shared_memory")
    store = StructuredMemoryStore(base / "memory.db")
    yield store
    store.clear()


def test_structured_store_can_be_created_in_worker_then_read_from_gui_thread(tmp_path):
    created: list[StructuredMemoryStore] = []

    worker = threading.Thread(
        target=lambda: created.append(
            StructuredMemoryStore(tmp_path / "memory.db")
        )
    )
    worker.start()
    worker.join()

    record = created[0].upsert(
        MemoryKind.PREFERENCE,
        "Anh prefers automatic memory.",
        None,
        "explicit preference",
        1.0,
        0.9,
    )

    assert created[0].get(record.id) == record


def test_store_creates_and_reads_active_record(shared_store):
    shared_store.clear()

    record = shared_store.upsert(
        kind=MemoryKind.PREFERENCE,
        content="Anh prefers Ru to address him as anh and itself as em.",
        scope=None,
        evidence="Anh thích Ru xưng em, gọi anh là anh.",
        confidence=1.0,
        importance=0.9,
    )

    assert record.status == "active"
    assert shared_store.get(record.id).content == record.content
    assert shared_store.count_active() == 1


def test_correction_supersedes_previous_active_record(shared_store):
    shared_store.clear()
    old = shared_store.upsert(
        MemoryKind.PREFERENCE,
        "Anh prefers brief replies.",
        None,
        "Anh thích câu trả lời ngắn.",
        1.0,
        0.7,
    )
    new = shared_store.upsert(
        MemoryKind.PREFERENCE,
        "Anh prefers detailed replies.",
        None,
        "Anh đổi ý, hãy trả lời chi tiết.",
        1.0,
        0.9,
        supersedes_id=old.id,
    )

    assert shared_store.get(old.id).status == "superseded"
    assert shared_store.get(new.id).status == "active"


def test_expired_unpinned_record_is_not_active(shared_store):
    shared_store.clear()
    shared_store.upsert(
        MemoryKind.PROJECT,
        "D:\\OldProject uses legacy tooling.",
        "D:\\OldProject",
        "old project",
        0.8,
        0.4,
        expires_at=datetime.now(UTC) - timedelta(seconds=1),
    )

    assert shared_store.count_active() == 0


def test_retrieve_prefers_exact_project_scope_and_obeys_context_budget(shared_store):
    shared_store.clear()
    shared_store.upsert(MemoryKind.PROJECT, "D:\\LLM uses a local GGUF runtime.", "D:\\LLM", "project note", 1.0, 0.9)
    shared_store.upsert(MemoryKind.PROJECT, "D:\\Other uses a hosted runtime.", "D:\\Other", "other note", 1.0, 0.9)

    records = shared_store.retrieve("Trong D:\\LLM runtime memory đang dùng gì?", limit=3, max_chars=140)

    assert [record.scope for record in records] == ["D:\\LLM"]
    assert len(shared_store.render_context(records, max_chars=140)) <= 140


def test_retrieve_returns_no_irrelevant_memory(shared_store):
    shared_store.clear()
    shared_store.upsert(MemoryKind.PREFERENCE, "Anh prefers brief replies.", None, "explicit", 1.0, 0.9)

    assert shared_store.retrieve("Thời tiết trên Sao Hoả thế nào?", limit=3, max_chars=400) == ()
