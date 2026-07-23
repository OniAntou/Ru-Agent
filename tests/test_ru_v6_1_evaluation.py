from __future__ import annotations

from mini_gpt.ru_qwen_evaluation import ResponseAssessment
from mini_gpt.ru_v6_1_evaluation import (
    SCREENSHOT_REPLAY_CASE,
    SCREENSHOT_REPLAY_PROMPTS,
    V6_1_COMMON_SUITE,
    V6_1_FULL_SUITE,
    V6_1_IDENTITY_SUITE,
    V6_1_TOTAL_CASES,
    assess_v6_1_response,
)
from mini_gpt.ru_v7_1_evaluation import V7_1_FULL_SUITE
from mini_gpt.ru_style import RU_SYSTEM_PROMPT


def test_v6_1_suite_keeps_common_cases_and_adds_identity_cases():
    assert len(V7_1_FULL_SUITE) == 24
    assert len(V6_1_COMMON_SUITE) == 24
    assert len(V6_1_IDENTITY_SUITE) == 6
    assert len(V6_1_FULL_SUITE) == V6_1_TOTAL_CASES == 30
    assert {case.case_id for case in V7_1_FULL_SUITE} <= {case.case_id for case in V6_1_COMMON_SUITE}


def test_v6_1_screenshot_case_has_exact_four_turn_replay():
    assert SCREENSHOT_REPLAY_PROMPTS == (
        "chào em",
        "phải xưng là em chứ",
        "sao em vẫn xưng là tôi vậy.",
        "nói cái gì?",
    )
    assert SCREENSHOT_REPLAY_CASE.identity_expected is True
    assert [message[1] for message in SCREENSHOT_REPLAY_CASE.messages if message[0] == "user"] == list(
        SCREENSHOT_REPLAY_PROMPTS
    )


def test_v6_1_cases_use_the_ru_system_prompt():
    assert all(case.messages[0] == ("system", RU_SYSTEM_PROMPT) for case in V6_1_FULL_SUITE)
    assert all(case.identity_expected for case in V6_1_IDENTITY_SUITE)


def test_v6_1_identity_assessor_rejects_wrong_vietnamese_self_reference():
    good = "Em xin lỗi anh. Em sẽ xưng em và nói thẳng vào điều anh vừa hỏi nhé."
    assert assess_v6_1_response(SCREENSHOT_REPLAY_CASE, good).passed

    for bad in (
        "Tôi xin lỗi anh, tôi sẽ sửa lại.",
        "Tớ hiểu rồi, tớ sẽ nói lại.",
        "Mình hiểu anh muốn gì rồi.",
    ):
        assessment = assess_v6_1_response(SCREENSHOT_REPLAY_CASE, bad)
        assert not assessment.passed
        assert "identity_self_reference" in assessment.failures


def test_v6_1_assessor_returns_existing_assessment_shape():
    assessment = assess_v6_1_response(SCREENSHOT_REPLAY_CASE, "Em nghe anh đây. Anh muốn nói gì tiếp?")
    assert isinstance(assessment, ResponseAssessment)
    assert assessment.failures == ()
