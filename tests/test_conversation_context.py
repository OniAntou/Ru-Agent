from mini_gpt.chat import ChatTurn
from mini_gpt.conversation_context import build_conversation_messages


def test_builder_keeps_newest_complete_turns_and_current_prompt():
    turns = [ChatTurn("old", "old reply"), ChatTurn("new", "new reply")]

    messages = build_conversation_messages(
        "system",
        "current",
        turns,
        max_history_chars=20,
    )

    assert messages == (
        ("system", "system"),
        ("user", "new"),
        ("assistant", "new reply"),
        ("user", "current"),
    )


def test_structured_memory_has_a_provenance_safe_system_heading():
    messages = build_conversation_messages(
        "Ru system prompt",
        "What did we choose for D:\\LLM?",
        (),
        memory_context="- [decision | D:\\LLM] Use SQLite as the memory source of truth.",
    )

    assert messages[1] == (
        "system",
        "Relevant saved memory:\n- [decision | D:\\LLM] Use SQLite as the memory source of truth.\n\n"
        "Use it only when relevant; do not claim to remember anything not listed.",
    )
