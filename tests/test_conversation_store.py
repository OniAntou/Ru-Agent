import pytest

from mini_gpt.conversation_store import ConversationStore


def test_store_appends_unicode_examples_and_creates_parent(tmp_path):
    path = tmp_path / "data" / "conversations" / "approved.jsonl"
    store = ConversationStore(path)

    first = store.save("Xin chào", "Chào anh", r"D:\LLM\checkpoints\latest.pt")
    second = store.save("Hello", "How are you?", r"D:\LLM\checkpoints\latest.pt")

    records = store.read_all()
    assert len(records) == 2
    assert records[0]["prompt"] == "Xin chào"
    assert records[0]["response"] == "Chào anh"
    assert records[0]["id"] == first.id
    assert records[1]["id"] == second.id
    assert path.read_text(encoding="utf-8").count("\n") == 2


def test_store_records_have_required_fields(tmp_path):
    record = ConversationStore(tmp_path / "approved.jsonl").save("prompt", "response", "checkpoint.pt")

    assert set(record.to_dict()) == {"id", "created_at_utc", "prompt", "response", "checkpoint"}
    assert record.created_at_utc.endswith("+00:00")


def test_store_rejects_blank_prompt_or_response(tmp_path):
    store = ConversationStore(tmp_path / "approved.jsonl")

    with pytest.raises(ValueError, match="prompt"):
        store.save(" ", "response", "checkpoint.pt")
    with pytest.raises(ValueError, match="response"):
        store.save("prompt", " ", "checkpoint.pt")


def test_read_all_returns_empty_for_missing_file(tmp_path):
    assert ConversationStore(tmp_path / "missing.jsonl").read_all() == []
