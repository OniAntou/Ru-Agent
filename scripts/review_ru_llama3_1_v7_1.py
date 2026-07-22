from __future__ import annotations

import argparse
import json
import os
import sys
from dataclasses import asdict
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.ru_qwen_evaluation import ResponseAssessment  # noqa: E402
from mini_gpt.ru_v7_1_evaluation import (  # noqa: E402
    V7_1ManualReview,
    record_v7_1_decision,
)


REVIEW_FIELDS = {
    "case_id",
    "natural",
    "relevant",
    "expected_language",
    "voice",
    "role_consistent",
    "severe_failure",
    "notes",
}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Record Ru Llama 3.1 v7.1 manual review and adoption decision")
    parser.add_argument("--evaluation", required=True)
    parser.add_argument("--reviews", required=True)
    parser.add_argument(
        "--output",
        default=str(
            PROJECT_ROOT
            / "checkpoints"
            / "ru-llama3.1-8b-instruct-v7.1"
            / "manual_review_v7_1.json"
        ),
    )
    parser.add_argument("--reviewer-notes", default="")
    return parser


def _load_reviews(path: Path) -> list[V7_1ManualReview]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    raw_reviews = payload.get("reviews") if isinstance(payload, dict) else payload
    if not isinstance(raw_reviews, list):
        raise ValueError("manual review input must be a list or an object containing reviews")
    reviews: list[V7_1ManualReview] = []
    for raw_review in raw_reviews:
        if not isinstance(raw_review, dict) or set(raw_review) != REVIEW_FIELDS:
            raise ValueError("manual review record has unexpected fields")
        if not all(isinstance(raw_review[field], bool) for field in REVIEW_FIELDS - {"case_id", "notes"}):
            raise ValueError("manual review gate fields must be boolean")
        if not isinstance(raw_review["case_id"], str) or not isinstance(raw_review["notes"], str):
            raise ValueError("manual review case_id and notes must be strings")
        reviews.append(V7_1ManualReview(**raw_review))
    return reviews


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    evaluation_path = Path(args.evaluation)
    evaluation = json.loads(evaluation_path.read_text(encoding="utf-8"))
    if not isinstance(evaluation, dict) or not isinstance(evaluation.get("cases"), list):
        raise ValueError("evaluation artifact must contain a cases list")
    assessments: dict[str, ResponseAssessment] = {}
    for case in evaluation["cases"]:
        if not isinstance(case, dict):
            raise ValueError("evaluation case must be an object")
        assessment = case.get("adapter_assessment")
        if not isinstance(assessment, dict) or not isinstance(assessment.get("passed"), bool):
            raise ValueError("evaluation case is missing adapter_assessment")
        failures = assessment.get("failures", [])
        if not isinstance(failures, list) or not all(isinstance(item, str) for item in failures):
            raise ValueError("evaluation failures must be a list of strings")
        case_id = case.get("case_id")
        if not isinstance(case_id, str):
            raise ValueError("evaluation case_id must be a string")
        assessments[case_id] = ResponseAssessment(assessment["passed"], tuple(failures))

    reviews = _load_reviews(Path(args.reviews))
    decision = record_v7_1_decision(
        assessments=assessments,
        reviews=reviews,
        physical_peak_mib=evaluation.get("physical_peak_mib"),
        physical_cap_mib=float(evaluation.get("physical_cap_mib", 6144)),
        reviewer_notes=args.reviewer_notes,
    )
    automatic_failed = sorted(case_id for case_id, assessment in assessments.items() if not assessment.passed)
    output = {
        "profile_name": decision["profile_name"],
        "phase": "manual_review",
        "suite_name": decision["suite_name"],
        "automatic": {
            "cases": len(assessments),
            "passed": len(assessments) - len(automatic_failed),
            "failed": len(automatic_failed),
            "failure_case_ids": automatic_failed,
            "peak_allocated_mib": evaluation.get("peak_allocated_mib"),
            "physical_peak_mib": evaluation.get("physical_peak_mib"),
            "verdict": evaluation.get("verdict"),
        },
        "manual_required_count": decision["manual_required_count"],
        "original_manual_required_count": decision["original_manual_required_count"],
        "manual_natural_count": decision["manual_natural_count"],
        "original_manual_natural_count": decision["original_manual_natural_count"],
        "reviews": [asdict(review) for review in reviews],
        "hardware": {
            "physical_cap_mib": decision["physical_cap_mib"],
            "physical_peak_mib": decision["physical_peak_mib"],
            "physical_cap_passed": decision["physical_cap_passed"],
        },
        "verdict": decision["verdict"],
        "failure_reasons": decision["failure_reasons"],
        "reviewer_notes": decision["reviewer_notes"],
    }
    destination = Path(args.output)
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_suffix(destination.suffix + ".partial")
    partial.write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(partial, destination)
    print(json.dumps({"output": str(destination), "verdict": output["verdict"], "failure_reasons": output["failure_reasons"]}, ensure_ascii=False, indent=2))
    return 0 if output["verdict"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
