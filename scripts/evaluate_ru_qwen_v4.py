from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.qwen_runtime import clear_qwen_generation_cache, generate_qwen_response, qwen_base_profile  # noqa: E402
from mini_gpt.ru_qwen_evaluation import V4_CONVERSATION_SUITE, assess_response  # noqa: E402


def bytes_to_mib(value: int) -> float:
    return round(value / (1024 * 1024), 3)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Evaluate Ru local Qwen base and optional adapter conversation quality")
    parser.add_argument("--config", default=str(PROJECT_ROOT / "configs" / "ru_qwen_v4_1_5b.json"))
    parser.add_argument("--adapter-dir")
    parser.add_argument("--output", default=str(PROJECT_ROOT / "checkpoints" / "ru-qwen-v4" / "evaluation.json"))
    return parser


def load_evaluation_config(path: str | Path) -> dict[str, object]:
    config = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(config, dict):
        raise ValueError("evaluation config must be a JSON object")
    base_model_id = config.get("base_model_id")
    if not isinstance(base_model_id, str) or not base_model_id:
        raise ValueError("evaluation config must include a non-empty base_model_id")
    profile_name = config.get("profile_name", base_model_id)
    phase = config.get("phase", "comparison")
    if not isinstance(profile_name, str) or not profile_name:
        raise ValueError("evaluation config profile_name must be a non-empty string")
    if not isinstance(phase, str) or not phase:
        raise ValueError("evaluation config phase must be a non-empty string")
    return {**config, "profile_name": profile_name, "phase": phase}


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    config = load_evaluation_config(args.config)
    profile = qwen_base_profile(config["base_model_id"])
    import torch

    if torch.cuda.is_available():
        torch.cuda.empty_cache()
        torch.cuda.reset_peak_memory_stats()
    base_results: dict[str, tuple[str, object]] = {}
    for case in V4_CONVERSATION_SUITE:
        base_response = generate_qwen_response(profile, case.messages)
        base_assessment = assess_response(case.prompt, base_response, case.language)
        base_results[case.case_id] = (base_response, base_assessment)
    base_automatic_gate_passed = all(assessment.passed for _, assessment in base_results.values())
    clear_qwen_generation_cache()
    adapter_results: dict[str, tuple[str, object]] = {}
    adapter_failed = False
    if args.adapter_dir:
        for case in V4_CONVERSATION_SUITE:
            adapter_response = generate_qwen_response(profile, case.messages, args.adapter_dir)
            adapter_assessment = assess_response(case.prompt, adapter_response, case.language)
            adapter_results[case.case_id] = (adapter_response, adapter_assessment)
            adapter_failed = adapter_failed or not adapter_assessment.passed
        clear_qwen_generation_cache()
    cases: list[dict[str, object]] = []
    for case in V4_CONVERSATION_SUITE:
        base_response, base_assessment = base_results[case.case_id]
        row: dict[str, object] = {
            "case_id": case.case_id,
            "language": case.language,
            "prompt": case.prompt,
            "base_response": base_response,
            "base_assessment": {"passed": base_assessment.passed, "failures": base_assessment.failures},
        }
        if case.case_id in adapter_results:
            adapter_response, adapter_assessment = adapter_results[case.case_id]
            row["adapter_response"] = adapter_response
            row["adapter_assessment"] = {"passed": adapter_assessment.passed, "failures": adapter_assessment.failures}
        cases.append(row)
    payload = {
        "profile_name": config["profile_name"],
        "phase": config["phase"],
        "base_model_id": profile.model_id,
        "adapter_dir": args.adapter_dir,
        "cases": cases,
        "peak_allocated_mib": bytes_to_mib(torch.cuda.max_memory_allocated()) if torch.cuda.is_available() else None,
        "verdict": "not_reviewed",
    }
    destination = Path(args.output)
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_suffix(destination.suffix + ".partial")
    partial.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(partial, destination)
    print(
        json.dumps(
            {
                "output": str(destination),
                "cases": len(cases),
                "base_automatic_gate_passed": base_automatic_gate_passed,
                "adapter_automatic_gate_passed": not adapter_failed,
                "peak_allocated_mib": payload["peak_allocated_mib"],
            },
            indent=2,
        )
    )
    return 1 if args.adapter_dir and adapter_failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
