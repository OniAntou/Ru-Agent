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
