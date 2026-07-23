from __future__ import annotations

import argparse
import json
import os
import sys
from dataclasses import replace
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.llama3_1_runtime import clear_llama_generation_cache, generate_llama_response_hf  # noqa: E402
from mini_gpt.llama3_1_training import PhysicalVramMonitor  # noqa: E402
from mini_gpt.ru_v6_1_evaluation import (  # noqa: E402
    SCREENSHOT_REPLAY_CASE,
    SCREENSHOT_REPLAY_PROMPTS,
    V6_1_FULL_SUITE,
    V6_1EvaluationCase,
    assess_v6_1_response,
)
from mini_gpt.ru_qwen_evaluation import ResponseAssessment  # noqa: E402

BASE_MODEL_ID = "NousResearch/Meta-Llama-3.1-8B-Instruct"


def bytes_to_mib(value: int) -> float:
    return round(value / (1024 * 1024), 3)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Evaluate Ru Llama 3.1 v6.1 natural conversation style")
    parser.add_argument("--base-model", default=str(PROJECT_ROOT / "models" / "llama3.1-8b-instruct-hf"))
    parser.add_argument(
        "--adapter-dir",
        default=str(PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v6.1"),
    )
    parser.add_argument(
        "--output",
        default=str(PROJECT_ROOT / "eval_results" / "ru_llama3_1_v6_1_style.json"),
    )
    parser.add_argument("--profile-name", default="ru_llama3_1_v6_1_style")
    parser.add_argument("--max-new-tokens", type=int, default=64)
    parser.add_argument("--physical-vram-cap-mib", type=float, default=6144)
    parser.add_argument("--physical-vram-poll-seconds", type=float, default=0.5)
    return parser


def _case_payload(case: V6_1EvaluationCase, response: str, assessment) -> dict[str, object]:
    return {
        "case_id": case.case_id,
        "language": case.language,
        "identity_expected": case.identity_expected,
        "messages": [{"role": role, "content": content} for role, content in case.messages],
        "prompt": case.prompt,
        "adapter_response": response,
        "adapter_assessment": {
            "passed": assessment.passed,
            "failures": assessment.failures,
        },
    }


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.max_new_tokens <= 0:
        raise ValueError("max_new_tokens must be positive")
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    import torch

    if torch.cuda.is_available():
        torch.cuda.empty_cache()
        torch.cuda.reset_peak_memory_stats()

    monitor = PhysicalVramMonitor(args.physical_vram_cap_mib, args.physical_vram_poll_seconds)
    monitor.start()
    cases: list[dict[str, object]] = []
    failed = False
    screenshot_steps: list[dict[str, object]] = []
    try:
        for case in V6_1_FULL_SUITE:
            if case.case_id == SCREENSHOT_REPLAY_CASE.case_id:
                continue
            response = generate_llama_response_hf(
                args.base_model,
                case.messages,
                args.adapter_dir,
                max_new_tokens=args.max_new_tokens,
                do_sample=False,
            )
            assessment = assess_v6_1_response(case, response)
            failed = failed or not assessment.passed
            cases.append(_case_payload(case, response, assessment))

        history: list[tuple[str, str]] = [("system", SCREENSHOT_REPLAY_CASE.messages[0][1])]
        for turn_index, prompt in enumerate(SCREENSHOT_REPLAY_PROMPTS, start=1):
            history.append(("user", prompt))
            response = generate_llama_response_hf(
                args.base_model,
                tuple(history),
                args.adapter_dir,
                max_new_tokens=args.max_new_tokens,
                do_sample=False,
            )
            step_case = replace(
                SCREENSHOT_REPLAY_CASE,
                case_id=f"{SCREENSHOT_REPLAY_CASE.case_id}_turn_{turn_index}",
                messages=tuple(history),
                prompt=prompt,
            )
            assessment = assess_v6_1_response(step_case, response)
            failed = failed or not assessment.passed
            screenshot_steps.append(_case_payload(step_case, response, assessment))
            history.append(("assistant", response))

        final_step = screenshot_steps[-1]
        replay_failures = tuple(
            dict.fromkeys(
                failure
                for step in screenshot_steps
                for failure in step["adapter_assessment"]["failures"]
            )
        )
        replay_assessment = ResponseAssessment(passed=not replay_failures, failures=replay_failures)
        cases.append(
            {
                "case_id": SCREENSHOT_REPLAY_CASE.case_id,
                "language": SCREENSHOT_REPLAY_CASE.language,
                "identity_expected": True,
                "messages": [
                    {"role": role, "content": content}
                    for role, content in SCREENSHOT_REPLAY_CASE.messages
                ],
                "prompt": SCREENSHOT_REPLAY_CASE.prompt,
                "adapter_response": final_step["adapter_response"],
                "adapter_assessment": {
                    "passed": replay_assessment.passed,
                    "failures": replay_assessment.failures,
                },
                "replay_automatic_gate_passed": replay_assessment.passed,
                "replay_turns": screenshot_steps,
            }
        )
    finally:
        clear_llama_generation_cache()
        monitor.stop()

    peak_allocated_mib = bytes_to_mib(torch.cuda.max_memory_allocated()) if torch.cuda.is_available() else None
    physical_peak_mib = monitor.peak_mib
    physical_cap_passed = physical_peak_mib is not None and physical_peak_mib <= args.physical_vram_cap_mib
    payload = {
        "profile_name": args.profile_name,
        "phase": "evaluation",
        "suite_name": "conversation_focus_v6_1_style",
        "base_model_id": BASE_MODEL_ID,
        "base_model_path": args.base_model,
        "adapter_dir": args.adapter_dir,
        "generation": {"do_sample": False, "max_new_tokens": args.max_new_tokens},
        "cases": cases,
        "case_count": len(cases),
        "screenshot_replay_prompts": list(SCREENSHOT_REPLAY_PROMPTS),
        "automatic_gate_passed": not failed,
        "peak_allocated_mib": peak_allocated_mib,
        "physical_peak_mib": physical_peak_mib,
        "physical_cap_mib": args.physical_vram_cap_mib,
        "physical_cap_passed": physical_cap_passed,
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
                "screenshot_turns": len(screenshot_steps),
                "automatic_gate_passed": not failed,
                "peak_allocated_mib": peak_allocated_mib,
                "physical_peak_mib": physical_peak_mib,
                "physical_cap_passed": physical_cap_passed,
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 0 if not failed and physical_cap_passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
