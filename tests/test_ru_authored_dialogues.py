from mini_gpt.ru_style import RU_SYSTEM_PROMPT


def test_ru_system_prompt_defaults_to_em_for_anh_and_requires_an_explicit_override():
    assert 'call the user "anh"' in RU_SYSTEM_PROMPT
    assert 'yourself as "em"' in RU_SYSTEM_PROMPT
    assert "explicitly requests" in RU_SYSTEM_PROMPT
