from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.ru_qwen_evaluation import assess_response  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Reassess saved Ru base responses without generating again")
    parser.add_argument("--evaluation", required=True, help="Existing base evaluation JSON output")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    evaluation_path = Path(args.evaluation)
    payload = json.loads(evaluation_path.read_text(encoding="utf-8"))
    if not isinstance(payload, dict) or not isinstance(payload.get("cases"), list):
        raise ValueError("evaluation JSON must contain a cases array")
    failures = 0
    for case in payload["cases"]:
        if not isinstance(case, dict):
            raise ValueError("evaluation cases must be objects")
        prompt, response, language = case.get("prompt"), case.get("base_response"), case.get("language")
        if not all(isinstance(value, str) for value in (prompt, response, language)):
            raise ValueError("evaluation cases require string prompt, base_response, and language")
        assessment = assess_response(prompt, response, language)
        case["base_assessment"] = {"passed": assessment.passed, "failures": list(assessment.failures)}
        failures += not assessment.passed
    payload.pop("decision", None)
    payload.pop("manual_review", None)
    payload["verdict"] = "not_reviewed"
    partial = evaluation_path.with_suffix(evaluation_path.suffix + ".partial")
    partial.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(partial, evaluation_path)
    print(json.dumps({"output": str(evaluation_path), "cases": len(payload["cases"]), "automatic_disqualifier_count": failures}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
