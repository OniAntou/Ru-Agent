from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.llama3_1_runtime import clear_llama_generation_cache, generate_llama_response_hf  # noqa: E402
from mini_gpt.ru_qwen_evaluation import (  # noqa: E402
    V7_CONVERSATION_SUITE,
    assess_conversation_focus_response,
)


BASE_MODEL_ID = "NousResearch/Meta-Llama-3.1-8B-Instruct"


def bytes_to_mib(value: int) -> float:
    return round(value / (1024 * 1024), 3)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Evaluate Ru Llama 3.1 v7 conversation focus")
    parser.add_argument("--base-model", default=str(PROJECT_ROOT / "models" / "llama3.1-8b-instruct-hf"))
    parser.add_argument("--adapter-dir", default=str(PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v7"))
    parser.add_argument(
        "--output",
        default=str(PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v7" / "conversation_focus_v7.json"),
    )
    parser.add_argument("--max-new-tokens", type=int, default=64)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    import torch

    if torch.cuda.is_available():
        torch.cuda.empty_cache()
        torch.cuda.reset_peak_memory_stats()

    results: dict[str, tuple[str, object]] = {}
    failed = False
    for case in V7_CONVERSATION_SUITE:
        response = generate_llama_response_hf(
            args.base_model,
            case.messages,
            args.adapter_dir,
            max_new_tokens=args.max_new_tokens,
            do_sample=False,
        )
        assessment = assess_conversation_focus_response(
            case.prompt,
            response,
            case.language,
            focus_expected=case.focus_expected,
            follow_up_expected=case.follow_up_expected,
        )
        results[case.case_id] = (response, assessment)
        failed = failed or not assessment.passed

    clear_llama_generation_cache()
    cases: list[dict[str, object]] = []
    for case in V7_CONVERSATION_SUITE:
        response, assessment = results[case.case_id]
        cases.append(
            {
                "case_id": case.case_id,
                "language": case.language,
                "prompt": case.prompt,
                "flirt_expected": case.flirt_expected,
                "focus_expected": case.focus_expected,
                "follow_up_expected": case.follow_up_expected,
                "adapter_response": response,
                "adapter_assessment": {"passed": assessment.passed, "failures": assessment.failures},
            }
        )

    payload = {
        "profile_name": "ru_llama3_1_qlora_v7",
        "phase": "evaluation",
        "suite_name": "conversation_focus_v7",
        "base_model_id": BASE_MODEL_ID,
        "base_model_path": args.base_model,
        "adapter_dir": args.adapter_dir,
        "generation": {"do_sample": False, "max_new_tokens": args.max_new_tokens},
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
                "adapter_automatic_gate_passed": not failed,
                "peak_allocated_mib": payload["peak_allocated_mib"],
            },
            indent=2,
        )
    )
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
