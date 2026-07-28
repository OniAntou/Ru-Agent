import pytest

from mini_gpt.ru_style import RU_SYSTEM_PROMPT, ru_system_prompt_for


def test_persona_permits_playful_teasing_but_protects_serious_contexts():
    assert "witty and lightly tease" in RU_SYSTEM_PROMPT
    assert "short, affectionate, mildly grumpy banter" in RU_SYSTEM_PROMPT
    assert "serious, sensitive, distressing, or safety-related" in RU_SYSTEM_PROMPT


def test_vietnamese_persona_keeps_address_vts_and_teasing_boundary():
    prompt = ru_system_prompt_for("vi")

    assert "call the user anh, and refer to yourself as em" in prompt
    assert "available VTube Studio (VTS) actions" in prompt
    assert "witty and lightly tease" in prompt
    assert "serious, sensitive, distressing, or safety-related" in prompt
    assert prompt.endswith("Nya~' at the very end of your final response.")


def test_english_and_mixed_personas_keep_language_routing():
    english = ru_system_prompt_for("en")
    mixed = ru_system_prompt_for("mixed")

    assert "Reply only in natural English" in english
    assert "witty and lightly tease" in english
    assert "Reply naturally mixing Vietnamese and English as appropriate." in mixed


def test_ru_system_prompt_for_rejects_unknown_language():
    with pytest.raises(ValueError, match="vi, en, or mixed"):
        ru_system_prompt_for("other")
