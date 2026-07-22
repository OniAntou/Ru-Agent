from __future__ import annotations

import pytest

from mini_gpt.ru_qwen_evaluation import ResponseAssessment, V7_CONVERSATION_SUITE
from mini_gpt.ru_v7_1_evaluation import (
    V7_1_FULL_SUITE,
    V7_1_REPAIR_SUITE,
    V7_1_TOTAL_CASES,
    V7_1ManualReview,
    record_v7_1_decision,
)


def _passing_assessments():
    return {
        case.case_id: ResponseAssessment(passed=True, failures=())
        for case in V7_1_FULL_SUITE
    }


def _passing_reviews():
    return [
        V7_1ManualReview(
            case_id=case.case_id,
            natural=True,
            relevant=True,
            expected_language=True,
            voice=True,
            role_consistent=True,
            severe_failure=False,
            notes="pass",
        )
        for case in V7_1_FULL_SUITE
    ]


def test_v7_1_suite_preserves_original_cases_and_adds_eight_repairs():
    assert len(V7_CONVERSATION_SUITE) == 16
    assert len(V7_1_REPAIR_SUITE) == 8
    assert len(V7_1_FULL_SUITE) == V7_1_TOTAL_CASES == 24
    assert {case.case_id for case in V7_CONVERSATION_SUITE} <= {
        case.case_id for case in V7_1_FULL_SUITE
    }


def test_v7_1_decision_requires_manual_review():
    decision = record_v7_1_decision(
        assessments=_passing_assessments(),
        reviews=None,
        physical_peak_mib=5600,
    )

    assert decision["verdict"] == "not_reviewed"
    assert decision["manual_natural_count"] is None


def test_v7_1_decision_passes_only_with_all_gates():
    decision = record_v7_1_decision(
        assessments=_passing_assessments(),
        reviews=_passing_reviews(),
        physical_peak_mib=5600,
    )

    assert decision["verdict"] == "passed"
    assert decision["manual_natural_count"] == 24
    assert decision["original_manual_natural_count"] == 16
    assert decision["physical_cap_passed"] is True


def test_v7_1_decision_rejects_automatic_failure_and_physical_overage():
    assessments = _passing_assessments()
    assessments["en_repair_quiet_evening"] = ResponseAssessment(
        passed=False,
        failures=("generic_short_reply",),
    )

    decision = record_v7_1_decision(
        assessments=assessments,
        reviews=_passing_reviews(),
        physical_peak_mib=6200,
    )

    assert decision["verdict"] == "not_adopted"
    assert "automatic_disqualifiers" in decision["failure_reasons"]
    assert "physical_cap_failed" in decision["failure_reasons"]


def test_v7_1_decision_rejects_missing_or_duplicate_reviews():
    reviews = _passing_reviews()
    with pytest.raises(ValueError, match="every evaluated case"):
        record_v7_1_decision(
            assessments=_passing_assessments(),
            reviews=reviews[:-1],
            physical_peak_mib=5600,
        )

    with pytest.raises(ValueError, match="duplicate"):
        record_v7_1_decision(
            assessments=_passing_assessments(),
            reviews=[*reviews, reviews[0]],
            physical_peak_mib=5600,
        )


def test_v7_1_decision_enforces_original_and_overall_manual_thresholds():
    reviews = _passing_reviews()
    reviews[0] = V7_1ManualReview(
        case_id=reviews[0].case_id,
        natural=False,
        relevant=False,
        expected_language=True,
        voice=True,
        role_consistent=True,
        severe_failure=False,
        notes="not natural",
    )
    reviews[1] = V7_1ManualReview(
        case_id=reviews[1].case_id,
        natural=False,
        relevant=True,
        expected_language=True,
        voice=True,
        role_consistent=True,
        severe_failure=True,
        notes="severe",
    )

    decision = record_v7_1_decision(
        assessments=_passing_assessments(),
        reviews=reviews,
        physical_peak_mib=5600,
    )

    assert decision["verdict"] == "not_adopted"
    assert "severe_manual_failure" in decision["failure_reasons"]
