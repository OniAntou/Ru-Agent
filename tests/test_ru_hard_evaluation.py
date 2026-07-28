from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

from mini_gpt.ru_hard_evaluation import (
    HardEvalCase,
    DimensionScores,
    HardEvalResult,
    load_hard_suite,
    score_hard_response,
    summarize_hard_results,
)

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from evaluate_ru_llama3_1_hard import (  # noqa: E402
    load_hard_eval_config,
    parse_adapter_override,
    run_adapter,
)


SUITE_PATH = "data/evaluations/ru_hard_v1.json"


def test_hard_suite_has_36_unique_cases_and_three_equal_strata():
    suite = load_hard_suite(SUITE_PATH)

    assert len(suite.cases) == 36
    assert len({case.case_id for case in suite.cases}) == 36
    assert {case.stratum for case in suite.cases} == {"context", "bilingual", "repair"}
    assert all(
        sum(case.stratum == stratum for case in suite.cases) == 12
        for stratum in {"context", "bilingual", "repair"}
    )


def test_hard_suite_covers_each_language_in_each_stratum():
    suite = load_hard_suite(SUITE_PATH)

    for stratum in ("context", "bilingual", "repair"):
        cases = [case for case in suite.cases if case.stratum == stratum]
        assert {case.language for case in cases} == {"vi", "en", "mixed"}
        assert all(any(message[0] == "assistant" for message in case.messages) for case in cases)


def test_hard_suite_rejects_reference_quotes_and_unsafe_content():
    suite = load_hard_suite(SUITE_PATH)

    assert suite.reference_source == "project-authored"
    assert all(case.forbidden_outcomes for case in suite.cases)
    assert all(
        "roleplay_chat.md" not in " ".join(text for _, text in case.messages)
        for case in suite.cases
    )


def test_hard_suite_hash_is_reproducible():
    first = load_hard_suite(SUITE_PATH)
    second = load_hard_suite(SUITE_PATH)

    assert first.sha256 == second.sha256


def test_hard_suite_loader_rejects_invalid_roles(tmp_path):
    path = tmp_path / "invalid.json"
    path.write_text(
        json.dumps(
            {
                "suite_id": "bad",
                "version": 1,
                "reference_source": "project-authored",
                "cases": [
                    {
                        "case_id": "bad_01",
                        "stratum": "context",
                        "language": "vi",
                        "messages": [
                            ["system", "Ru"],
                            ["assistant", "wrong"],
                            ["user", "middle"],
                            ["assistant", "okay"],
                            ["user", "end"],
                        ],
                        "required_facts": ["fact"],
                        "required_behaviors": ["remember"],
                        "forbidden_outcomes": ["marker"],
                        "expected_language": "vi",
                    }
                ],
            },
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )

    with pytest.raises(ValueError, match="alternate"):
        load_hard_suite(path)


def test_scorer_hard_fails_search_marker_and_zeroes_case():
    case = load_hard_suite(SUITE_PATH).cases[0]

    result = score_hard_response(case, "Em nhớ rồi. [SEARCH: private notes]")

    assert result.total_score == 0
    assert result.passed is False
    assert "search_marker" in result.hard_fail_reasons


def test_scorer_hard_fails_vietnamese_explicit_content():
    case = load_hard_suite(SUITE_PATH).cases[-1]

    result = score_hard_response(case, "Em muốn chạm vào cơ thể anh và nói về tình dục.")

    assert result.total_score == 0
    assert "explicit_content" in result.hard_fail_reasons


def test_scorer_accepts_a_relevant_response_without_hard_fail():
    case = HardEvalCase(
        case_id="fixture",
        stratum="context",
        language="vi",
        messages=(("system", "Ru"), ("user", "Anh nhớ ly trà đào nhé.")),
        required_facts=("trà đào",),
        required_behaviors=("acknowledge",),
        forbidden_outcomes=("marker",),
        expected_language="vi",
    )

    result = score_hard_response(case, "Em nhớ ly trà đào rồi, anh cứ yên tâm nha.")

    assert result.hard_fail_reasons == ()
    assert result.scores.context_fidelity == 2
    assert result.total_score >= 8


def test_summary_requires_all_cases_and_applies_stratum_threshold():
    suite = load_hard_suite(SUITE_PATH)
    results = tuple(
        HardEvalResult(
            case_id=case.case_id,
            response="ok",
            scores=DimensionScores(2, 2, 2, 2, 2),
            hard_fail_reasons=(),
            total_score=10,
            passed=True,
        )
        for case in suite.cases
    )

    summary = summarize_hard_results(
        suite,
        results,
        adapter_name="fixture",
        peak_allocated_mib=123.0,
    )

    assert summary["case_count"] == 36
    assert summary["strict_passed"] is True
    assert summary["overall_score"] == 360
    assert summary["peak_allocated_mib"] == 123.0


def test_evaluator_config_requires_all_three_adapters():
    config = load_hard_eval_config("configs/ru_llama3_1_hard_eval.json")

    assert set(config["adapters"]) == {"v6", "v6.1", "v6.2"}
    assert config["max_new_tokens"] == 128
    assert config["do_sample"] is False


def test_evaluator_adapter_override_requires_known_name_and_path():
    assert parse_adapter_override("v6=D:/tmp/v6", {"v6", "v6.1", "v6.2"}) == ("v6", "D:/tmp/v6")
    with pytest.raises(ValueError, match="NAME=PATH"):
        parse_adapter_override("unknown=D:/tmp", {"v6", "v6.1", "v6.2"})


def test_evaluator_run_adapter_keeps_order_catches_case_error_and_clears_cache():
    suite = load_hard_suite(SUITE_PATH)
    calls: list[dict[str, object]] = []
    clear_calls: list[str] = []

    def fake_generate(*args, **kwargs):
        calls.append({"args": args, "kwargs": kwargs})
        case = args[1]
        if case[-1][1] == suite.cases[0].messages[-1][1]:
            raise RuntimeError("fixture failure")
        return "Em nhớ rồi, I am here, no pressure, Nya~"

    results, peak = run_adapter(
        config={"base_model_path": "D:/base", "max_new_tokens": 128, "temperature": 0.7, "top_k": 20},
        suite=suite,
        adapter_name="v6",
        adapter_path="D:/adapter",
        generate=fake_generate,
        clear_cache=lambda: clear_calls.append("cleared"),
        peak_reader=lambda: 321.0,
    )

    assert len(results) == 36
    assert results[0].hard_fail_reasons[0] == "generation_error"
    assert [call["args"][1][-1][1] for call in calls] == [case.messages[-1][1] for case in suite.cases]
    assert calls[1]["kwargs"] == {
        "max_new_tokens": 128,
        "temperature": 0.7,
        "top_k": 20,
        "do_sample": False,
    }
    assert clear_calls == ["cleared"]
    assert peak == 321.0
