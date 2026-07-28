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
    PROACTIVE_FLIRT_SUITE,
    assess_proactive_flirt_response,
)

def bytes_to_mib(value: int) -> float:
    return round(value / (1024 * 1024), 3)

def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Evaluate Ru Llama 3.1 QLoRA adapter")
    parser.add_argument("--base-model", default=str(PROJECT_ROOT / "models" / "llama3.1-8b-instruct-hf"))
    parser.add_argument("--adapter-dir", default=str(PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v6"))
    parser.add_argument("--output", default=str(PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v6" / "evaluation.json"))
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

    adapter_results: dict[str, tuple[str, object]] = {}
    adapter_failed = False

    if args.adapter_dir:
        for case in PROACTIVE_FLIRT_SUITE:
            adapter_response = generate_llama_response_hf(
                args.base_model,
                case.messages,
                args.adapter_dir,
                max_new_tokens=args.max_new_tokens,
                do_sample=False,
            )
            adapter_assessment = assess_proactive_flirt_response(case.prompt, adapter_response, case.language)
            adapter_results[case.case_id] = (adapter_response, adapter_assessment)
            adapter_failed = adapter_failed or not adapter_assessment.passed
        clear_llama_generation_cache()

    cases: list[dict[str, object]] = []
    for case in PROACTIVE_FLIRT_SUITE:
        row: dict[str, object] = {
            "case_id": case.case_id,
            "language": case.language,
            "prompt": case.prompt,
            "flirt_expected": case.flirt_expected,
        }
        if case.case_id in adapter_results:
            adapter_response, adapter_assessment = adapter_results[case.case_id]
            row["adapter_response"] = adapter_response
            row["adapter_assessment"] = {"passed": adapter_assessment.passed, "failures": adapter_assessment.failures}
        cases.append(row)

    payload = {
        "profile_name": "ru_llama3_1_qlora",
        "phase": "evaluation",
        "suite_name": "proactive_flirt_v6",
        "base_model_id": "NousResearch/Meta-Llama-3.1-8B-Instruct",
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
                "adapter_automatic_gate_passed": not adapter_failed,
                "peak_allocated_mib": payload["peak_allocated_mib"],
            },
            indent=2,
        )
    )
    return 1 if args.adapter_dir and adapter_failed else 0

if __name__ == "__main__":
    raise SystemExit(main())
