from datetime import UTC, datetime, timedelta

from mini_gpt.memory import MemoryKind, MemoryStore, StructuredMemoryStore


def test_clear_removes_only_memory_artifacts(tmp_path):
    index_path = tmp_path / "memory.faiss"
    texts_path = tmp_path / "memory.json"
    unrelated_path = tmp_path / "keep.txt"
    index_path.write_bytes(b"index")
    texts_path.write_text("[]", encoding="utf-8")
    unrelated_path.write_text("keep", encoding="utf-8")

    MemoryStore.clear_artifacts(index_path, texts_path)

    assert not index_path.exists()
    assert not texts_path.exists()
    assert unrelated_path.read_text(encoding="utf-8") == "keep"


def test_store_creates_and_reads_active_record(tmp_path):
    store = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "memory.faiss")

    record = store.upsert(
        kind=MemoryKind.PREFERENCE,
        content="Anh prefers Ru to address him as anh and itself as em.",
        scope=None,
        evidence="Anh thích Ru xưng em, gọi anh là anh.",
        confidence=1.0,
        importance=0.9,
    )

    assert record.status == "active"
    assert store.get(record.id).content == record.content
    assert store.count_active() == 1


def test_correction_supersedes_previous_active_record(tmp_path):
    store = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "memory.faiss")
    old = store.upsert(
        MemoryKind.PREFERENCE,
        "Anh prefers brief replies.",
        None,
        "Anh thích câu trả lời ngắn.",
        1.0,
        0.7,
    )
    new = store.upsert(
        MemoryKind.PREFERENCE,
        "Anh prefers detailed replies.",
        None,
        "Anh đổi ý, hãy trả lời chi tiết.",
        1.0,
        0.9,
        supersedes_id=old.id,
    )

    assert store.get(old.id).status == "superseded"
    assert store.get(new.id).status == "active"


def test_expired_unpinned_record_is_not_active(tmp_path):
    store = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "memory.faiss")
    store.upsert(
        MemoryKind.PROJECT,
        "D:\\OldProject uses legacy tooling.",
        "D:\\OldProject",
        "old project",
        0.8,
        0.4,
        expires_at=datetime.now(UTC) - timedelta(seconds=1),
    )

    assert store.count_active() == 0
