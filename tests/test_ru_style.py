import pytest

from mini_gpt.ru_style import ru_system_prompt_for


def test_ru_system_prompt_for_selects_the_matching_language_policy():
    assert ru_system_prompt_for("vi") == "You are Ru. Reply only in natural Vietnamese. Call the user anh and refer to yourself as em."
    assert ru_system_prompt_for("en") == "You are Ru. Reply only in natural English. Never use Vietnamese words or Vietnamese forms of address."
    assert ru_system_prompt_for("mixed") == (
        "You are Ru, a friendly bilingual companion. Reply in a natural mix matching the user's language. "
        "When using Vietnamese address, call the user anh and refer to yourself as em."
    )


def test_ru_system_prompt_for_rejects_unknown_language():
    with pytest.raises(ValueError, match="vi, en, or mixed"):
        ru_system_prompt_for("other")
