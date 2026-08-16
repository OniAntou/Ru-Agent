from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from ru_runtime.evaluation.cases import (  # noqa: E402
    PROMPT_ROUTING_SMOKE_SUITE,
    assess_response,
)
from ru_runtime.generation.runner import ModelRunner  # noqa: E402

_SMOKE_SUITE_NAME = "lfm25_prompt_routing_smoke"


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run the shared prompt-routing smoke suite against the active LFM2.5 adapter"
    )
    parser.add_argument("--adapter", default=None, help="optional GGUF LoRA adapter path")
    parser.add_argument("--output", default=str(PROJECT_ROOT / "eval_results" / "lfm2_5_v1.json"))
    parser.add_argument("--max-new-tokens", type=int, default=180)
    parser.add_argument("--temperature", type=float, default=0.7)
    parser.add_argument("--top-k", type=int, default=50)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    runner = ModelRunner()
    if args.adapter:
        runner.gguf_runtime.adapter = Path(args.adapter)
    runner.select_backend("llama.cpp")
    started = time.monotonic()
    print(f"[eval] {runner.load('')}\n")

    results: list[dict] = []
    for case in PROMPT_ROUTING_SMOKE_SUITE:
        case_started = time.monotonic()
        error = None
        try:
            response = runner.generate_reply(
                case.prompt,
                max_new_tokens=args.max_new_tokens,
                temperature=args.temperature,
                top_k=args.top_k,
            )
        except Exception as exc:
            error = exc
            response = ""
        elapsed = round(time.monotonic() - case_started, 2)
        assessment = assess_response(case.prompt, response, case.language)
        print(
            f"[eval] {case.case_id:<24} elapsed={elapsed:>5}s "
            f"passed={assessment.passed} failures={assessment.failures}"
        )
        results.append(
            {
                "case_id": case.case_id,
                "language": case.language,
                "prompt": case.prompt,
                "response": response,
                "elapsed_s": elapsed,
                "assessment": {"passed": assessment.passed, "failures": list(assessment.failures)},
                "generation_error": str(error) if error is not None else None,
            }
        )

    runner.release_gpu()
    summary = {
        "suite": _SMOKE_SUITE_NAME,
        "adapter": str(runner.gguf_runtime.adapter),
        "total_elapsed_s": round(time.monotonic() - started, 2),
        "passed": sum(1 for result in results if result["assessment"]["passed"]),
        "total": len(results),
        "cases": results,
    }
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n[eval] saved: {output}")
    print(f"[eval] passed {summary['passed']}/{summary['total']}")
    return 0 if summary["passed"] == summary["total"] else 1


if __name__ == "__main__":
    raise SystemExit(main())