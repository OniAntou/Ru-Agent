from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))
sys.path.insert(0, str(PROJECT_ROOT / "scripts"))

from evaluate_ru_qwen_v4 import bytes_to_mib, load_evaluation_config  # noqa: E402
from mini_gpt.qwen_runtime import clear_qwen_generation_cache, generate_qwen_response, qwen_base_profile  # noqa: E402
from mini_gpt.ru_qwen_evaluation import PROMPT_ROUTING_SMOKE_SUITE, assess_response  # noqa: E402


def hardware_gate(peak_allocated_mib: float | None, peak_limit_mib: float) -> dict[str, float | bool | None]:
    return {
        "passed": peak_allocated_mib is not None and peak_allocated_mib <= peak_limit_mib,
        "peak_allocated_mib": peak_allocated_mib,
        "peak_limit_mib": peak_limit_mib,
    }


def positive_float(value: str) -> float:
    parsed = float(value)
    if parsed <= 0:
        raise argparse.ArgumentTypeError("value must be positive")
    return parsed


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Prompt-routing smoke evaluation for Ru local Qwen conversation quality")
    parser.add_argument("--config", default=str(PROJECT_ROOT / "configs" / "ru_qwen3_4b.json"))
    parser.add_argument(
        "--output",
        default=str(PROJECT_ROOT / "checkpoints" / "ru-qwen3-4b" / "prompt_routing_smoke.json"),
    )
    parser.add_argument("--max-peak-mib", type=positive_float, default=5500.0)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    config = load_evaluation_config(args.config)
    profile = qwen_base_profile(config["base_model_id"])
    import torch

    results: list[tuple[object, str, object]] = []
    hardware_failure: str | None = None
    if not torch.cuda.is_available():
        hardware_failure = "cuda_unavailable"
    else:
        torch.cuda.empty_cache()
        torch.cuda.reset_peak_memory_stats()
        try:
            for case in PROMPT_ROUTING_SMOKE_SUITE:
                try:
                    response = generate_qwen_response(profile, case.messages)
                except torch.OutOfMemoryError:
                    hardware_failure = "out_of_memory"
                    break
                results.append((case, response, assess_response(case.prompt, response, case.language)))
                peak_allocated_mib = bytes_to_mib(torch.cuda.max_memory_allocated())
                if peak_allocated_mib > args.max_peak_mib:
                    hardware_failure = "peak_vram_exceeded"
                    break
        finally:
            clear_qwen_generation_cache()
    cases = [
        {
            "case_id": case.case_id,
            "language": case.language,
            "system_prompt": case.messages[0][1],
            "prompt": case.prompt,
            "base_response": response,
            "base_assessment": {"passed": assessment.passed, "failures": list(assessment.failures)},
        }
        for case, response, assessment in results
    ]
    peak_allocated_mib = bytes_to_mib(torch.cuda.max_memory_allocated()) if torch.cuda.is_available() else None
    hardware = hardware_gate(peak_allocated_mib, args.max_peak_mib)
    automatic_gate_passed = len(results) == len(PROMPT_ROUTING_SMOKE_SUITE) and all(
        assessment.passed for _, _, assessment in results
    )
    payload = {
        "profile_name": config["profile_name"],
        "phase": "prompt_routing_smoke",
        "prompt_policy": "language_specific_v1",
        "base_model_id": profile.model_id,
        "cases": cases,
        "peak_allocated_mib": peak_allocated_mib,
        "hardware_gate": hardware,
        "verdict": "not_adopted" if hardware_failure else "not_reviewed",
    }
    if hardware_failure:
        payload["decision"] = {
            "phase": "hardware",
            "failure_reasons": [hardware_failure],
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
                "automatic_gate_passed": automatic_gate_passed,
                "peak_allocated_mib": payload["peak_allocated_mib"],
                "hardware_gate_passed": hardware["passed"],
                "hardware_failure": hardware_failure,
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 1 if hardware_failure else 0


if __name__ == "__main__":
    raise SystemExit(main())
