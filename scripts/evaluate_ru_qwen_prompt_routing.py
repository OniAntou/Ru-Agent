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


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Prompt-routing smoke evaluation for Ru local Qwen3 conversation quality")
    parser.add_argument("--config", default=str(PROJECT_ROOT / "configs" / "ru_qwen3_4b.json"))
    parser.add_argument(
        "--output",
        default=str(PROJECT_ROOT / "checkpoints" / "ru-qwen3-4b" / "prompt_routing_smoke.json"),
    )
    return parser


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
    results: list[tuple[object, str, object]] = []
    try:
        for case in PROMPT_ROUTING_SMOKE_SUITE:
            response = generate_qwen_response(profile, case.messages)
            results.append((case, response, assess_response(case.prompt, response, case.language)))
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
    automatic_gate_passed = all(assessment.passed for _, _, assessment in results)
    payload = {
        "profile_name": config["profile_name"],
        "phase": "prompt_routing_smoke",
        "prompt_policy": "language_specific_v1",
        "base_model_id": profile.model_id,
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
                "automatic_gate_passed": automatic_gate_passed,
                "peak_allocated_mib": payload["peak_allocated_mib"],
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
