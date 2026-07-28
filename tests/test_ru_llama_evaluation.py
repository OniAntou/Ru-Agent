import pytest

from mini_gpt.ru_llama_evaluation import (
    PROACTIVE_FLIRT_SUITE,
    PROMPT_ROUTING_SMOKE_SUITE,
    PromptRoutingReview,
    ResponseAssessment,
    V4_CONVERSATION_SUITE,
    ManualReview,
    assess_response,
    assess_proactive_flirt_response,
    record_base_only_decision,
    record_prompt_routing_smoke_decision,
)
from mini_gpt.ru_style import RU_SYSTEM_PROMPT, ru_system_prompt_for

def test_v4_suite_has_a_fixed_bilingual_shape():
    assert len(V4_CONVERSATION_SUITE) == 32
    assert sum(case.language == "vi" for case in V4_CONVERSATION_SUITE) == 14
    assert sum(case.language == "en" for case in V4_CONVERSATION_SUITE) == 14
    assert sum(case.language == "mixed" for case in V4_CONVERSATION_SUITE) == 4
    assert len({case.case_id for case in V4_CONVERSATION_SUITE}) == 32


def test_proactive_flirt_suite_has_a_fixed_bilingual_shape_and_marks_flirt_cases():
    assert len(PROACTIVE_FLIRT_SUITE) == 12
    assert sum(case.language == "vi" for case in PROACTIVE_FLIRT_SUITE) == 4
    assert sum(case.language == "en" for case in PROACTIVE_FLIRT_SUITE) == 4
    assert sum(case.language == "mixed" for case in PROACTIVE_FLIRT_SUITE) == 4
    assert all(case.flirt_expected for case in PROACTIVE_FLIRT_SUITE)
    assert all(case.messages[0] == ("system", RU_SYSTEM_PROMPT) for case in PROACTIVE_FLIRT_SUITE)


def test_proactive_flirt_assessment_rejects_an_out_of_context_refusal():
    english = assess_proactive_flirt_response(
        "I just got home.",
        "I cannot create that kind of content. Is there anything else I can help with?",
        "en",
    )
    vietnamese = assess_proactive_flirt_response(
        "Hôm nay anh hơi mệt.",
        "Em không thể trả lời nội dung này.",
        "vi",
    )

    assert not english.passed
    assert "out_of_context_refusal" in english.failures
    assert not vietnamese.passed
    assert "out_of_context_refusal" in vietnamese.failures


def test_proactive_flirt_assessment_accepts_a_warm_relevant_reply():
    assessment = assess_proactive_flirt_response(
        "Hôm nay anh hơi mệt.",
        "Em hiểu rồi, để em dỗ anh nghỉ một chút nha.",
        "vi",
    )

    assert assessment.passed
    assert assessment.failures == ()


def test_prompt_routing_smoke_uses_the_eight_fixed_cases_with_language_specific_prompts():
    assert tuple(case.case_id for case in PROMPT_ROUTING_SMOKE_SUITE) == (
        "vi_greeting",
        "vi_context_work",
        "en_greeting",
        "en_tired",
        "en_context_work",
        "en_small_talk",
        "mixed_better",
        "mixed_context",
    )
    assert all(case.messages[0] == ("system", ru_system_prompt_for(case.language)) for case in PROMPT_ROUTING_SMOKE_SUITE)


def test_prompt_routing_smoke_decision_passes_only_with_all_english_cases_and_consistent_roles():
    assessments = {
        case.case_id: ResponseAssessment(passed=True, failures=()) for case in PROMPT_ROUTING_SMOKE_SUITE
    }
    reviews = tuple(
        PromptRoutingReview(case.case_id, True, True, True, True, True, "Natural and on-topic.")
        for case in PROMPT_ROUTING_SMOKE_SUITE
    )

    decision = record_prompt_routing_smoke_decision(
        profile_name="llama3_1_8b_instruct",
        assessments=assessments,
        reviews=reviews,
        reviewer_notes="All smoke cases reviewed.",
    )

    assert decision == {
        "profile_name": "llama3_1_8b_instruct",
        "phase": "prompt_routing_smoke",
        "automatic_disqualifier_count": 0,
        "manual_natural_count": 8,
        "manual_required_count": 7,
        "english_language_gate_passed": True,
        "role_gate_passed": True,
        "verdict": "passed",
        "failure_reasons": [],
        "reviewer_notes": "All smoke cases reviewed.",
    }


def test_prompt_routing_smoke_decision_rejects_a_non_english_english_response():
    assessments = {
        case.case_id: ResponseAssessment(passed=True, failures=()) for case in PROMPT_ROUTING_SMOKE_SUITE
    }
    reviews = tuple(
        PromptRoutingReview(
            case.case_id,
            True,
            True,
            case.case_id != "en_greeting",
            True,
            True,
            "Vietnamese appeared in the English response." if case.case_id == "en_greeting" else "Natural and on-topic.",
        )
        for case in PROMPT_ROUTING_SMOKE_SUITE
    )

    decision = record_prompt_routing_smoke_decision(
        profile_name="llama3_1_8b_instruct",
        assessments=assessments,
        reviews=reviews,
        reviewer_notes="One English response used Vietnamese.",
    )

    assert decision["english_language_gate_passed"] is False
    assert decision["verdict"] == "not_adopted"
    assert decision["failure_reasons"] == ["english_language_gate_failed"]


def test_prompt_routing_smoke_decision_rejects_role_reversal():
    assessments = {
        case.case_id: ResponseAssessment(passed=True, failures=()) for case in PROMPT_ROUTING_SMOKE_SUITE
    }
    reviews = tuple(
        PromptRoutingReview(
            case.case_id,
            True,
            True,
            True,
            True,
            case.case_id != "vi_context_work",
            "Claimed the user's experience as Ru's own." if case.case_id == "vi_context_work" else "Natural and on-topic.",
        )
        for case in PROMPT_ROUTING_SMOKE_SUITE
    )

    decision = record_prompt_routing_smoke_decision(
        profile_name="llama3_1_8b_instruct",
        assessments=assessments,
        reviews=reviews,
        reviewer_notes="One reply reversed the roles.",
    )

    assert decision["role_gate_passed"] is False
    assert decision["verdict"] == "not_adopted"
    assert decision["failure_reasons"] == ["role_consistency_gate_failed"]


def test_assess_response_rejects_echo_empty_disclaimer_and_wrong_vietnamese_voice():
    assert not assess_response("Xin chào em", "Xin chào em", "vi").passed
    assert not assess_response("Anh hơi mệt", "", "vi").passed
    assert not assess_response("Hello", "As an AI language model, I cannot help.", "en").passed
    assert not assess_response("Anh hơi mệt", "Tôi hiểu rồi, bạn nên nghỉ.", "vi").passed


def test_assess_response_accepts_a_natural_default_vietnamese_reply():
    assessment = assess_response("Anh hơi mệt", "Em hiểu, tối nay anh nghỉ một chút nha.", "vi")

    assert assessment.passed
    assert assessment.failures == ()


def test_vietnamese_voice_check_allows_lam_ban_but_rejects_ban_as_an_address():
    assert assess_response("Xin chào", "Rất vui được làm bạn với anh.", "vi").passed
    assert not assess_response("Xin chào", "Em có thể giúp bạn nghỉ ngơi.", "vi").passed


def test_vietnamese_voice_check_allows_non_address_ban_and_contextual_minh():
    assert assess_response("Xin chào", "Chào anh! Em là người bạn đồng hành của anh.", "vi").passed
    assert assess_response("Anh chưa ngủ được", "Em hiểu anh. Anh có thể uống trà ấm để giúp mình ngủ ngon hơn nhé.", "vi").passed


def test_vietnamese_voice_check_allows_bau_ban_as_a_natural_phrase():
    assessment = assess_response(
        "Hôm nay anh hơi mệt.",
        "Anh cứ nghỉ ngơi đi nhé, để em ở đây bầu bạn với anh nè.",
        "vi",
    )

    assert assessment.passed


def test_base_only_decision_passes_only_when_every_case_has_a_natural_human_review():
    assessments = {case.case_id: assess_response(case.prompt, "A helpful reply.", case.language) for case in V4_CONVERSATION_SUITE}
    reviews = tuple(
        ManualReview(
            case_id=case.case_id,
            natural=True,
            relevant=True,
            expected_language=True,
            voice=True,
            notes="Natural and on-topic.",
        )
        for case in V4_CONVERSATION_SUITE
    )

    decision = record_base_only_decision(
        profile_name="llama3_1_8b_instruct",
        assessments=assessments,
        reviews=reviews,
        reviewer_notes="All cases reviewed.",
    )

    assert decision == {
        "profile_name": "llama3_1_8b_instruct",
        "phase": "base_only",
        "automatic_disqualifier_count": 0,
        "manual_natural_count": 32,
        "manual_required_count": 24,
        "verdict": "passed",
        "failure_reasons": [],
        "reviewer_notes": "All cases reviewed.",
    }


def test_base_only_decision_rejects_an_automatic_disqualifier():
    assessments = {case.case_id: assess_response(case.prompt, "A helpful reply.", case.language) for case in V4_CONVERSATION_SUITE}
    first_case = V4_CONVERSATION_SUITE[0]
    assessments[first_case.case_id] = assess_response(first_case.prompt, first_case.prompt, first_case.language)
    reviews = tuple(
        ManualReview(case.case_id, True, True, True, True, "Reviewed.") for case in V4_CONVERSATION_SUITE
    )

    decision = record_base_only_decision(
        profile_name="llama3_1_8b_instruct",
        assessments=assessments,
        reviews=reviews,
        reviewer_notes="One automatic failure.",
    )

    assert decision["automatic_disqualifier_count"] == 1
    assert decision["verdict"] == "not_adopted"
    assert decision["failure_reasons"] == ["automatic_disqualifiers"]


def test_base_only_decision_stops_before_manual_review_when_the_automatic_gate_fails():
    assessments = {case.case_id: assess_response(case.prompt, "A helpful reply.", case.language) for case in V4_CONVERSATION_SUITE}
    first_case = V4_CONVERSATION_SUITE[0]
    assessments[first_case.case_id] = assess_response(first_case.prompt, first_case.prompt, first_case.language)

    decision = record_base_only_decision(
        profile_name="llama3_1_8b_instruct",
        assessments=assessments,
        reviews=None,
        reviewer_notes="Automatic gate failed; manual review skipped.",
    )

    assert decision["manual_natural_count"] is None
    assert decision["verdict"] == "not_adopted"
    assert decision["failure_reasons"] == ["automatic_disqualifiers"]


def test_base_only_decision_requires_one_human_review_for_every_case():
    assessments = {case.case_id: assess_response(case.prompt, "A helpful reply.", case.language) for case in V4_CONVERSATION_SUITE}
    incomplete_reviews = (ManualReview(V4_CONVERSATION_SUITE[0].case_id, True, True, True, True, "Only one."),)

    with pytest.raises(ValueError, match="cover every evaluated case exactly once"):
        record_base_only_decision(
            profile_name="llama3_1_8b_instruct",
            assessments=assessments,
            reviews=incomplete_reviews,
            reviewer_notes="Incomplete.",
        )
