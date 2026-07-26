from mini_gpt.memory import MemoryStore


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
