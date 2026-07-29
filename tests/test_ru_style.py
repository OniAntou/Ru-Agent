import re

from mini_gpt.ru_v6_3_dialogues import AUTHORED_ROWS
from mini_gpt.ru_style import RU_CLOUD_SYSTEM_PROMPT, RU_PROMPTS, RU_SYSTEM_PROMPT


_EMOJI = re.compile(r"[\U0001F300-\U0001FAFF]")
_EMOTICON = re.compile(r"(?<!\w)(?::[-^']?[)D(;]|;[-^']?\)|<3)(?!\w)")


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


def test_ru_prompts_route_emotion_to_vts_without_chat_symbols() -> None:
    prompts = [RU_SYSTEM_PROMPT, *RU_PROMPTS.values(), RU_CLOUD_SYSTEM_PROMPT]
    assert all("Do not use emoji" in prompt for prompt in prompts)
    assert all("ASCII emoticons" in prompt for prompt in prompts)
    assert all("VTS action" in prompt for prompt in prompts)
    assert all("private VTS markers" in prompt for prompt in prompts)


def test_ru_prompts_explain_identity_mission_and_capabilities() -> None:
    prompts = [RU_SYSTEM_PROMPT, *RU_PROMPTS.values(), RU_CLOUD_SYSTEM_PROMPT]
    for prompt in prompts:
        folded = prompt.casefold()
        assert "trợ lý ai của anh" in folded
        assert "avatar Live2D" in prompt
        assert "nhiệm vụ" in folded
        assert "VTube Studio" in prompt
        assert "không tự nhận" in folded


def test_ru_prompts_describe_connected_avatar_controls_without_mouth_override() -> None:
    prompts = [RU_SYSTEM_PROMPT, *RU_PROMPTS.values(), RU_CLOUD_SYSTEM_PROMPT]
    for prompt in prompts:
        assert "hotkey" in prompt
        assert "expression" in prompt
        assert "VoiceVolume" in prompt
        assert "LLM_MouthOpen" in prompt
        assert "metadata VTS ẩn" in prompt


def test_v6_3_authored_rows_have_no_chat_symbols() -> None:
    text = "\n".join(
        str(message["content"])
        for row in AUTHORED_ROWS
        for message in row["messages"][1:]
    ) + "\n" + "\n".join(str(row["response"]) for row in AUTHORED_ROWS)
    assert _EMOJI.search(text) is None
    assert _EMOTICON.search(text) is None
