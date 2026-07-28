from datetime import UTC, datetime, timedelta
import json

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


def test_migration_imports_only_safe_legacy_blocks(tmp_path):
    legacy_path = tmp_path / "memory_texts.json"
    legacy_path.write_text(
        json.dumps(
            [
                "User: Anh thích Ru tự động hoá.\nAssistant: Em nhớ rồi.",
                "User: API key là sk-secret.\nAssistant: Đã nhận.",
            ],
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )
    store = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "memory.faiss")

    assert store.migrate_legacy_transcripts(legacy_path) == {"imported": 1, "rejected": 1}
    assert store.count_active() == 1
    assert legacy_path.exists()


def test_retrieve_prefers_exact_project_scope_and_obeys_context_budget(tmp_path):
    store = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "memory.faiss")
    store.upsert(MemoryKind.PROJECT, "D:\\LLM uses a local GGUF runtime.", "D:\\LLM", "project note", 1.0, 0.9)
    store.upsert(MemoryKind.PROJECT, "D:\\Other uses a hosted runtime.", "D:\\Other", "other note", 1.0, 0.9)

    records = store.retrieve("Trong D:\\LLM runtime memory đang dùng gì?", limit=3, max_chars=140)

    assert [record.scope for record in records] == ["D:\\LLM"]
    assert len(store.render_context(records, max_chars=140)) <= 140


def test_retrieve_returns_no_irrelevant_memory(tmp_path):
    store = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "memory.faiss")
    store.upsert(MemoryKind.PREFERENCE, "Anh prefers brief replies.", None, "explicit", 1.0, 0.9)

    assert store.retrieve("Thời tiết trên Sao Hoả thế nào?", limit=3, max_chars=400) == ()
