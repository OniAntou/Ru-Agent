from __future__ import annotations

import pytest

from ru_runtime.memory import StructuredMemoryStore


def test_operations_after_close_raise_descriptive_error(tmp_path) -> None:
    store = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "index.bin")
    store.close()

    with pytest.raises(RuntimeError, match="closed"):
        store.count_active()

    with pytest.raises(RuntimeError, match="closed"):
        store.list_active()

    with pytest.raises(RuntimeError, match="closed"):
        store.retrieve("anything")


def test_memory_can_be_reopened_after_close(tmp_path) -> None:
    store = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "index.bin")
    store.close()
    reopened = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "index.bin")
    assert reopened.count_active() == 0
    reopened.close()