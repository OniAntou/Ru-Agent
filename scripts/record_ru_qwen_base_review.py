from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.ru_qwen_evaluation import ManualReview, ResponseAssessment, record_base_only_decision  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Record a manual Ru base-only evaluation decision")
    parser.add_argument("--evaluation", required=True, help="Existing evaluator JSON output")
    parser.add_argument("--review", required=True, help="Manual review JSON with reviewer_notes and reviews")
    return parser


def _load_json(path: str | Path) -> dict[str, object]:
    payload = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(payload, dict):
        raise ValueError(f"JSON object expected: {path}")
    return payload


def _reviews(payload: dict[str, object]) -> tuple[ManualReview, ...]:
    raw_reviews = payload.get("reviews")
    if not isinstance(raw_reviews, list):
        raise ValueError("review JSON must contain a reviews array")
    reviews: list[ManualReview] = []
    for raw_review in raw_reviews:
        if not isinstance(raw_review, dict):
            raise ValueError("every manual review must be an object")
        try:
            review = ManualReview(
                case_id=raw_review["case_id"],
                natural=raw_review["natural"],
                relevant=raw_review["relevant"],
                expected_language=raw_review["expected_language"],
                voice=raw_review["voice"],
                notes=raw_review["notes"],
            )
        except KeyError as error:
            raise ValueError(f"manual review is missing {error.args[0]}") from error
        if not isinstance(review.case_id, str) or not isinstance(review.notes, str):
            raise ValueError("manual review case_id and notes must be strings")
        if not all(isinstance(value, bool) for value in (review.natural, review.relevant, review.expected_language, review.voice)):
            raise ValueError("manual review quality fields must be booleans")
        reviews.append(review)
    return tuple(reviews)


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    evaluation_path = Path(args.evaluation)
    evaluation = _load_json(evaluation_path)
    cases = evaluation.get("cases")
    if not isinstance(cases, list):
        raise ValueError("evaluation JSON must contain cases")
    assessments: dict[str, ResponseAssessment] = {}
    for case in cases:
        if not isinstance(case, dict) or not isinstance(case.get("case_id"), str) or not isinstance(case.get("base_assessment"), dict):
            raise ValueError("evaluation cases must contain case_id and base_assessment")
        assessment = case["base_assessment"]
        passed, failures = assessment.get("passed"), assessment.get("failures")
        if not isinstance(passed, bool) or not isinstance(failures, list) or not all(isinstance(failure, str) for failure in failures):
            raise ValueError("base_assessment must contain boolean passed and string failures")
        assessments[case["case_id"]] = ResponseAssessment(passed=passed, failures=tuple(failures))
    review_payload = _load_json(args.review)
    reviewer_notes = review_payload.get("reviewer_notes", "")
    if not isinstance(reviewer_notes, str):
        raise ValueError("reviewer_notes must be a string")
    decision = record_base_only_decision(
        profile_name=str(evaluation.get("profile_name", evaluation.get("base_model_id", "unknown"))),
        assessments=assessments,
        reviews=_reviews(review_payload),
        reviewer_notes=reviewer_notes,
    )
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
