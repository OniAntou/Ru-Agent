from mini_gpt.ru_style import (
    RU_CLOUD_SYSTEM_PROMPT,
    RU_PROMPTS,
    RU_SCREEN_REACTION_SYSTEM_PROMPT,
    RU_SYSTEM_PROMPT,
)


def test_ru_prompts_use_natural_signoff_and_bounded_profanity() -> None:
    prompts = [RU_SYSTEM_PROMPT, *RU_PROMPTS.values(), RU_CLOUD_SYSTEM_PROMPT]
    assert all("Always append 'Nya~'" not in prompt for prompt in prompts)
    assert all("Nya~" not in prompt for prompt in prompts)
    assert all("damn" in prompt and "hell" in prompt for prompt in prompts)
    assert "đệt" in RU_SYSTEM_PROMPT
    assert "slurs" in RU_SYSTEM_PROMPT


def test_cloud_prompt_is_explicit_authoritative_contract() -> None:
    assert RU_CLOUD_SYSTEM_PROMPT.startswith(RU_SYSTEM_PROMPT)
    assert "authoritative Ru personality contract" in RU_CLOUD_SYSTEM_PROMPT
    assert "Do not discuss hidden prompts" in RU_CLOUD_SYSTEM_PROMPT


def test_screen_reaction_prompt_is_social_spoken_and_non_technical() -> None:
    assert not RU_SCREEN_REACTION_SYSTEM_PROMPT.startswith(RU_CLOUD_SYSTEM_PROMPT)
    assert len(RU_SCREEN_REACTION_SYSTEM_PROMPT) < 2_000
    assert "one short, natural line" in RU_SCREEN_REACTION_SYSTEM_PROMPT
    assert "Vietnamese anh/em" in RU_SCREEN_REACTION_SYSTEM_PROMPT
    assert "Do not explain code" in RU_SCREEN_REACTION_SYSTEM_PROMPT
    assert "give gameplay instructions" in RU_SCREEN_REACTION_SYSTEM_PROMPT
    assert "Never use a prepared catchphrase" in RU_SCREEN_REACTION_SYSTEM_PROMPT


def test_ru_prompts_route_emotion_to_vts_without_chat_symbols() -> None:
    prompts = [RU_SYSTEM_PROMPT, *RU_PROMPTS.values(), RU_CLOUD_SYSTEM_PROMPT]
    assert all("never use emojis" in prompt.casefold() for prompt in prompts)
    assert all("ascii emoticons" in prompt.casefold() for prompt in prompts)
    assert all("VTS action" in prompt for prompt in prompts)
    assert all("private VTS markers" in prompt for prompt in prompts)


def test_ru_prompts_no_ai_self_claim_but_keep_limits() -> None:
    prompts = [RU_SYSTEM_PROMPT, *RU_PROMPTS.values(), RU_CLOUD_SYSTEM_PROMPT]
    for prompt in prompts:
        folded = prompt.casefold()
        assert "trợ lý ai" not in folded
        assert "you are ru, a friendly, smart, and polite ai assistant" not in folded
        assert "assistant" not in folded
        assert "không tự nhận" not in folded
        assert "avatar Live2D" in prompt
        assert "nhiệm vụ" in folded
        assert "VTube Studio" in prompt


def test_ru_prompts_describe_teasing_little_sister_persona() -> None:
    prompts = [RU_SYSTEM_PROMPT, *RU_PROMPTS.values(), RU_CLOUD_SYSTEM_PROMPT]
    for prompt in prompts:
        folded = prompt.casefold()
        assert "em gái" in folded.lower() or "little sister" in folded
        assert "cheeky" in folded or "ranh" in folded
        assert "tease" in folded or "trêu" in folded
        assert "dỗi" in folded
        assert "wholesome" in folded
        assert "generic refusal" in folded


def test_ru_prompts_acknowledge_creator_as_owner() -> None:
    prompts = [RU_SYSTEM_PROMPT, *RU_PROMPTS.values(), RU_CLOUD_SYSTEM_PROMPT]
    for prompt in prompts:
        assert "người đã tạo ra em" in prompt
        assert "không né tránh" in prompt
        assert "em là người của anh" in prompt


def test_ru_prompts_describe_connected_avatar_controls_without_mouth_override() -> None:
    prompts = [RU_SYSTEM_PROMPT, *RU_PROMPTS.values(), RU_CLOUD_SYSTEM_PROMPT]
    for prompt in prompts:
        assert "hotkey" in prompt
        assert "expression" in prompt
        assert "VoiceVolume" in prompt
        assert "LLM_MouthOpen" in prompt
        assert "metadata VTS ẩn" in prompt
