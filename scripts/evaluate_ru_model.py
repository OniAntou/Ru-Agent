from __future__ import annotations

import argparse
import json
import sys
import time
from dataclasses import asdict
from datetime import datetime, timezone
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from ru_runtime.evaluation.judge import (  # noqa: E402
    JudgeScorer,
    ModelReport,
    compare_models,
    load_config,
    make_generate_fn,
    resolve_suite,
    run_suite,
)
from ru_runtime.generation.runner import ModelRunner  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run the Ru judge evaluation on one or more GGUF models")
    parser.add_argument("--config", default=str(PROJECT_ROOT / "configs" / "ru_judge_eval.json"))
    parser.add_argument("--models", default="v1", help="comma-separated model ids from config")
    parser.add_argument("--suite", default="v5", choices=("v5", "smoke", "flirt"))
    parser.add_argument("--output-dir", default=str(PROJECT_ROOT / "eval_results" / "judge"))
    parser.add_argument("--no-judge", action="store_true", help="skip the LLM judge entirely")
    parser.add_argument("--judge-model", default=None, help="override the judge model id")
    return parser


def _save_report(report: ModelReport, output_dir: Path) -> Path:
    model_dir = output_dir / report.model_id
    model_dir.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    path = model_dir / f"{timestamp}.json"
    path.write_text(json.dumps(asdict(report), ensure_ascii=False, indent=2), encoding="utf-8")
    latest = model_dir / "_latest.json"
    latest.write_text(json.dumps(asdict(report), ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[judge] saved report: {path}")
    return path


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    config = load_config(args.config)
    weights = config["weights"]
    pass_threshold = float(config["pass_threshold"])
    min_pass_rate = float(config["min_pass_rate"])
    delta_tolerance = float(config["baseline_delta_tolerance"])
    gen_config = config["generation"]
    models = config["models"]
    suite = resolve_suite(args.suite)

    judge_scorer: JudgeScorer | None = None
    if not args.no_judge:
        judge_model = args.judge_model or config["judge"]["model"]
        try:
            judge_scorer = JudgeScorer(model=judge_model)
        except ValueError as exc:
            print(f"[judge] warning: {exc}; running without judge")
            judge_scorer = None

    output_dir = Path(args.output_dir)
    started = time.monotonic()
    reports = []
    runner: ModelRunner | None = None
    for model_id in [model.strip() for model in args.models.split(",")]:
        if model_id not in models:
            print(f"[judge] error: unknown model id {model_id!r}")
            return 2
        spec = models[model_id]
        print(f"[judge] evaluating model {model_id!r} (adapter={spec['adapter']})")
        if runner is None:
            runner = ModelRunner()
        else:
            runner.release_gpu()
        runner.gguf_runtime.adapter = Path(spec["adapter"]) if spec["adapter"] else None
        runner.select_backend("llama.cpp")
        print(f"[judge] {runner.load('')}")
        generate = make_generate_fn(runner, gen_config)
        case_results = run_suite(
            suite,
            generate,
            gen_config,
            judge_scorer,
            weights,
            pass_threshold=pass_threshold,
        )
        report = ModelReport(model_id=model_id, adapter=spec["adapter"], suite=args.suite, case_results=case_results)
        print(
            f"[judge] {model_id}: total={report.total} pass_rate={report.pass_rate:.2f} "
            f"disqualifiers={report.disqualifier_count} judge_errors={report.judge_error_count}"
        )
        _save_report(report, output_dir)
        reports.append(report)

    if runner is not None:
        runner.release_gpu()

    if len(reports) >= 2:
        baseline = reports[0]
        compare_dir = output_dir / "_compare"
        compare_dir.mkdir(parents=True, exist_ok=True)
        timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        comparisons = []
        for candidate in reports[1:]:
            comparison = compare_models(
                baseline,
                candidate,
                min_pass_rate=min_pass_rate,
                delta_tolerance=delta_tolerance,
            )
            comparisons.append(comparison)
            print(
                f"[judge] compare {candidate.model_id} vs {baseline.model_id}: "
                f"delta={comparison.total_delta} passed={comparison.passed} reasons={comparison.failure_reasons}"
            )
        bundle = {
            "suite": args.suite,
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "total_elapsed_s": round(time.monotonic() - started, 2),
            "comparisons": [
                {
                    "baseline": baseline.model_id,
                    "candidate": comparison.candidate.model_id,
                    "total_delta": comparison.total_delta,
                    "dimension_deltas": comparison.dimension_deltas,
                    "passed": comparison.passed,
                    "failure_reasons": comparison.failure_reasons,
                }
                for comparison in comparisons
            ],
        }
        compare_path = compare_dir / f"{timestamp}.json"
        compare_path.write_text(json.dumps(bundle, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"[judge] saved comparison: {compare_path}")
        if not all(comparison.passed for comparison in comparisons):
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
