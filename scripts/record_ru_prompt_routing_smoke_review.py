from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.ru_qwen_evaluation import (  # noqa: E402
    PromptRoutingReview,
    ResponseAssessment,
    record_prompt_routing_smoke_decision,
)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Record a manual Ru prompt-routing smoke decision")
    parser.add_argument("--evaluation", required=True, help="Existing prompt-routing smoke evaluator JSON output")
    review_mode = parser.add_mutually_exclusive_group(required=True)
    review_mode.add_argument("--review", help="Manual review JSON with reviewer_notes and reviews")
    review_mode.add_argument("--automatic-only", action="store_true", help="Record an automatic-gate rejection without manual review")
    return parser


def _load_json(path: str | Path) -> dict[str, object]:
    payload = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(payload, dict):
        raise ValueError(f"JSON object expected: {path}")
    return payload


def _reviews(payload: dict[str, object]) -> tuple[PromptRoutingReview, ...]:
    raw_reviews = payload.get("reviews")
    if not isinstance(raw_reviews, list):
        raise ValueError("review JSON must contain a reviews array")
    reviews: list[PromptRoutingReview] = []
    for raw_review in raw_reviews:
        if not isinstance(raw_review, dict):
            raise ValueError("every manual review must be an object")
        try:
            review = PromptRoutingReview(
                case_id=raw_review["case_id"],
                natural=raw_review["natural"],
                relevant=raw_review["relevant"],
                expected_language=raw_review["expected_language"],
                voice=raw_review["voice"],
                role_consistent=raw_review["role_consistent"],
                notes=raw_review["notes"],
            )
        except KeyError as error:
            raise ValueError(f"manual review is missing {error.args[0]}") from error
        if not isinstance(review.case_id, str) or not isinstance(review.notes, str):
            raise ValueError("manual review case_id and notes must be strings")
        quality_fields = (
            review.natural,
            review.relevant,
            review.expected_language,
            review.voice,
            review.role_consistent,
        )
        if not all(isinstance(value, bool) for value in quality_fields):
            raise ValueError("manual review quality fields must be booleans")
        reviews.append(review)
    return tuple(reviews)


def _assessments(evaluation: dict[str, object]) -> dict[str, ResponseAssessment]:
    cases = evaluation.get("cases")
    if not isinstance(cases, list):
        raise ValueError("evaluation JSON must contain cases")
    assessments: dict[str, ResponseAssessment] = {}
    for case in cases:
        if not isinstance(case, dict) or not isinstance(case.get("case_id"), str) or not isinstance(case.get("base_assessment"), dict):
            raise ValueError("evaluation cases must contain case_id and base_assessment")
        case_id = case["case_id"]
        if case_id in assessments:
            raise ValueError("evaluation contains duplicate case ids")
        assessment = case["base_assessment"]
        passed, failures = assessment.get("passed"), assessment.get("failures")
        if not isinstance(passed, bool) or not isinstance(failures, list) or not all(isinstance(failure, str) for failure in failures):
            raise ValueError("base_assessment must contain boolean passed and string failures")
        assessments[case_id] = ResponseAssessment(passed=passed, failures=tuple(failures))
    return assessments


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    evaluation_path = Path(args.evaluation)
    evaluation = _load_json(evaluation_path)
    review_payload: dict[str, object] | None = None
    reviewer_notes = "Automatic gate failed; manual review skipped."
    reviews: tuple[PromptRoutingReview, ...] | None = None
    if args.review:
        review_payload = _load_json(args.review)
        reviewer_notes = review_payload.get("reviewer_notes", "")
        if not isinstance(reviewer_notes, str):
            raise ValueError("reviewer_notes must be a string")
        reviews = _reviews(review_payload)
    decision = record_prompt_routing_smoke_decision(
        profile_name=str(evaluation.get("profile_name", evaluation.get("base_model_id", "unknown"))),
        assessments=_assessments(evaluation),
        reviews=reviews,
        reviewer_notes=reviewer_notes,
    )
    if review_payload is not None:
        evaluation["manual_review"] = review_payload
    evaluation["decision"] = decision
    evaluation["verdict"] = decision["verdict"]
    partial = evaluation_path.with_suffix(evaluation_path.suffix + ".partial")
    partial.write_text(json.dumps(evaluation, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(partial, evaluation_path)
    print(json.dumps(decision, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
