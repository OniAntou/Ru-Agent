import subprocess
import sys
from pathlib import Path

import pytest

from mini_gpt.ru_qwen_evaluation import (
    V4_CONVERSATION_SUITE,
    ManualReview,
    assess_response,
    record_base_only_decision,
)

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "evaluate_ru_qwen_v4.py"


def test_v4_suite_has_a_fixed_bilingual_shape():
    assert len(V4_CONVERSATION_SUITE) == 32
    assert sum(case.language == "vi" for case in V4_CONVERSATION_SUITE) == 14
    assert sum(case.language == "en" for case in V4_CONVERSATION_SUITE) == 14
    assert sum(case.language == "mixed" for case in V4_CONVERSATION_SUITE) == 4
    assert len({case.case_id for case in V4_CONVERSATION_SUITE}) == 32


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
        profile_name="ru_qwen_v5_3b",
        assessments=assessments,
        reviews=reviews,
        reviewer_notes="All cases reviewed.",
    )

    assert decision == {
        "profile_name": "ru_qwen_v5_3b",
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
        profile_name="ru_qwen_v5_3b",
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
        profile_name="ru_qwen_v5_3b",
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
            profile_name="ru_qwen_v5_3b",
            assessments=assessments,
            reviews=incomplete_reviews,
            reviewer_notes="Incomplete.",
        )


def test_evaluator_cli_exposes_base_and_adapter_arguments_without_loading_a_model():
    result = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True, check=False)

    assert result.returncode == 0
    assert "Evaluate Ru local Qwen base and optional adapter conversation quality" in result.stdout
    assert "--adapter-dir" in result.stdout
