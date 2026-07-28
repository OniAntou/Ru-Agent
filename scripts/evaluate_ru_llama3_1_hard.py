from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Callable

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.llama3_1_runtime import clear_llama_generation_cache, generate_llama_response_hf  # noqa: E402
from mini_gpt.ru_hard_evaluation import (  # noqa: E402
    DimensionScores,
    HardEvalCase,
    HardEvalResult,
    HardEvalSuite,
    hard_result_to_dict,
    load_hard_suite,
    score_hard_response,
    summarize_hard_results,
)


def load_hard_eval_config(path: str | Path) -> dict[str, object]:
    payload = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(payload, dict):
        raise ValueError("evaluation config must be a JSON object")
    for field in ("suite_path", "base_model_path", "output_dir"):
        if not isinstance(payload.get(field), str) or not payload[field]:
            raise ValueError(f"evaluation config must include {field}")
    adapters = payload.get("adapters")
    if not isinstance(adapters, dict) or set(adapters) != {"v6", "v6.1", "v6.2"}:
        raise ValueError("evaluation config adapters must contain v6, v6.1, and v6.2")
    if not all(isinstance(value, str) and value for value in adapters.values()):
        raise ValueError("evaluation config adapter paths must be non-empty strings")
    max_new_tokens = payload.get("max_new_tokens", 128)
    if not isinstance(max_new_tokens, int) or max_new_tokens <= 0:
        raise ValueError("max_new_tokens must be positive")
    do_sample = payload.get("do_sample", False)
    if do_sample is not False:
        raise ValueError("hard evaluation requires do_sample=false")
    temperature = payload.get("temperature", 0.7)
    top_k = payload.get("top_k", 20)
    if not isinstance(temperature, (int, float)) or temperature <= 0:
        raise ValueError("temperature must be positive")
    if not isinstance(top_k, int) or top_k < 0:
        raise ValueError("top_k cannot be negative")
    return {**payload, "max_new_tokens": max_new_tokens, "temperature": temperature, "top_k": top_k}


def parse_adapter_override(value: str, known_names: set[str]) -> tuple[str, str]:
    name, separator, path = value.partition("=")
    if not separator or name not in known_names or not path:
        raise ValueError("adapter override must use NAME=PATH for a known adapter")
    return name, path


def _cuda_peak_allocated_mib() -> float | None:
    try:
        import torch
    except ImportError:
        return None
    return round(torch.cuda.max_memory_allocated() / (1024 * 1024), 3) if torch.cuda.is_available() else None


def _reset_cuda_peak() -> None:
    try:
        import torch
    except ImportError:
        return
    if torch.cuda.is_available():
        torch.cuda.empty_cache()
        torch.cuda.reset_peak_memory_stats()


def _generation_error(case: HardEvalCase, error: Exception) -> HardEvalResult:
    return HardEvalResult(
        case_id=case.case_id,
        response="",
        scores=DimensionScores(0, 0, 0, 0, 0),
        hard_fail_reasons=("generation_error", f"{type(error).__name__}: {error}"),
        total_score=0,
        passed=False,
    )


def run_adapter(
    *,
    config: dict[str, object],
    suite: HardEvalSuite,
    adapter_name: str,
    adapter_path: str,
    generate: Callable[..., str] = generate_llama_response_hf,
    clear_cache: Callable[[], None] = clear_llama_generation_cache,
    peak_reader: Callable[[], float | None] = _cuda_peak_allocated_mib,
) -> tuple[list[HardEvalResult], float | None]:
    try:
        _reset_cuda_peak()
    except Exception:
        pass
    results: list[HardEvalResult] = []
    try:
        for case in suite.cases:
            try:
                response = generate(
                    str(config["base_model_path"]),
                    case.messages,
                    adapter_path,
                    max_new_tokens=int(config["max_new_tokens"]),
                    temperature=float(config["temperature"]),
                    top_k=int(config["top_k"]),
                    do_sample=False,
                )
                results.append(score_hard_response(case, response))
            except Exception as error:
                results.append(_generation_error(case, error))
    finally:
        try:
            clear_cache()
        except Exception:
            pass
    return results, peak_reader()


def _write_json(destination: Path, payload: dict[str, object]) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_suffix(destination.suffix + ".partial")
    partial.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(partial, destination)


def evaluate_config(
    config: dict[str, object],
    *,
    output_dir: str | Path | None = None,
    adapter_names: tuple[str, ...] = ("v6", "v6.1", "v6.2"),
) -> dict[str, object]:
    suite = load_hard_suite(str(config["suite_path"]))
    destination = Path(output_dir or str(config["output_dir"]))
    adapters = config["adapters"]
    assert isinstance(adapters, dict)
    summaries: dict[str, dict[str, object]] = {}
    for adapter_name in adapter_names:
        try:
            results, peak_allocated_mib = run_adapter(
                config=config,
                suite=suite,
                adapter_name=adapter_name,
                adapter_path=str(adapters[adapter_name]),
            )
        except Exception as error:
            results = [_generation_error(case, error) for case in suite.cases]
            peak_allocated_mib = None
        summary = summarize_hard_results(
            suite,
            results,
            adapter_name=adapter_name,
            peak_allocated_mib=peak_allocated_mib,
        )
        summaries[adapter_name] = summary
        _write_json(
            destination / f"{adapter_name}_hard_v1.json",
            {
                "suite": {"suite_id": suite.suite_id, "version": suite.version, "sha256": suite.sha256},
                "adapter_name": adapter_name,
                "adapter_path": str(adapters[adapter_name]),
                "generation": {"max_new_tokens": config["max_new_tokens"], "do_sample": False, "temperature": config["temperature"], "top_k": config["top_k"]},
                "summary": summary,
                "cases": [
                    {
                        "case_id": case.case_id,
                        "stratum": case.stratum,
                        "language": case.language,
                        "prompt": case.messages[-1][1],
                        **hard_result_to_dict(result),
                    }
                    for case, result in zip(suite.cases, results, strict=True)
                ],
            },
        )
    ranking = sorted(
        summaries.values(),
        key=lambda summary: (
            bool(summary["strict_passed"]),
            float(summary["overall_pass_rate"]),
            float(summary["overall_score"]) / float(summary["overall_max_score"]),
        ),
        reverse=True,
    )
    comparison = {
        "suite_id": suite.suite_id,
        "suite_version": suite.version,
        "suite_sha256": suite.sha256,
        "generation": {"max_new_tokens": config["max_new_tokens"], "do_sample": False, "temperature": config["temperature"], "top_k": config["top_k"]},
        "summaries": summaries,
        "ranking": [summary["adapter_name"] for summary in ranking],
        "promotion": "manual_review_required",
    }
    _write_json(destination / "comparison_hard_v1.json", comparison)
    return comparison


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run Ru's 36-case hard adapter comparison")
    parser.add_argument("--config", default=str(PROJECT_ROOT / "configs" / "ru_llama3_1_hard_eval.json"))
    parser.add_argument("--adapter", action="append", default=[])
    parser.add_argument("--only", action="append", choices=("v6", "v6.1", "v6.2"), default=[])
    parser.add_argument("--output-dir")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    config = load_hard_eval_config(args.config)
    adapters = dict(config["adapters"])
    for override in args.adapter:
        name, path = parse_adapter_override(override, set(adapters))
        adapters[name] = path
    config["adapters"] = adapters
    adapter_names = tuple(args.only) if args.only else ("v6", "v6.1", "v6.2")
    result = evaluate_config(config, output_dir=args.output_dir, adapter_names=adapter_names)
    print(json.dumps({"output_dir": args.output_dir or config["output_dir"], "ranking": result["ranking"], "suite_sha256": result["suite_sha256"]}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
