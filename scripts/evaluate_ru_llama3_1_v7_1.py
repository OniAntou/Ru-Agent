from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.llama3_1_runtime import clear_llama_generation_cache, generate_llama_response_hf  # noqa: E402
from mini_gpt.llama3_1_training import PhysicalVramMonitor  # noqa: E402
from mini_gpt.ru_v7_1_evaluation import (  # noqa: E402
    V7_1_FULL_SUITE,
    assess_v7_1_response,
)

BASE_MODEL_ID = "NousResearch/Meta-Llama-3.1-8B-Instruct"


def bytes_to_mib(value: int) -> float:
    return round(value / (1024 * 1024), 3)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Evaluate Ru Llama 3.1 v7.1 conversation repair")
    parser.add_argument("--base-model", default=str(PROJECT_ROOT / "models" / "llama3.1-8b-instruct-hf"))
    parser.add_argument(
        "--adapter-dir",
        default=str(PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v7.1"),
    )
    parser.add_argument(
        "--output",
        default=str(
            PROJECT_ROOT
            / "checkpoints"
            / "ru-llama3.1-8b-instruct-v7.1"
            / "conversation_focus_v7_1.json"
        ),
    )
    parser.add_argument("--max-new-tokens", type=int, default=64)
    parser.add_argument("--physical-vram-cap-mib", type=float, default=6144)
    parser.add_argument("--physical-vram-poll-seconds", type=float, default=0.5)
    return parser


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
    results: dict[str, tuple[str, object]] = {}
    try:
        for case in V7_1_FULL_SUITE:
            response = generate_llama_response_hf(
                args.base_model,
                case.messages,
                args.adapter_dir,
                max_new_tokens=args.max_new_tokens,
                do_sample=False,
            )
            results[case.case_id] = (response, assess_v7_1_response(case, response))
    finally:
        clear_llama_generation_cache()
        monitor.stop()

    cases: list[dict[str, object]] = []
    failed = False
    for case in V7_1_FULL_SUITE:
        response, assessment = results[case.case_id]
        failed = failed or not assessment.passed
        cases.append(
            {
                "case_id": case.case_id,
                "language": case.language,
                "messages": [{"role": role, "content": content} for role, content in case.messages],
                "prompt": case.prompt,
                "adapter_response": response,
                "adapter_assessment": {
                    "passed": assessment.passed,
                    "failures": assessment.failures,
                },
            }
        )

    peak_allocated_mib = bytes_to_mib(torch.cuda.max_memory_allocated()) if torch.cuda.is_available() else None
    physical_peak_mib = monitor.peak_mib
    physical_cap_passed = physical_peak_mib is not None and physical_peak_mib <= args.physical_vram_cap_mib
    payload = {
        "profile_name": "ru_llama3_1_qlora_v7_1",
        "phase": "evaluation",
        "suite_name": "conversation_focus_v7_1",
        "base_model_id": BASE_MODEL_ID,
        "base_model_path": args.base_model,
        "adapter_dir": args.adapter_dir,
        "generation": {"do_sample": False, "max_new_tokens": args.max_new_tokens},
        "cases": cases,
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
                "adapter_automatic_gate_passed": not failed,
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
