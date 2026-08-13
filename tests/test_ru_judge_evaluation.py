from __future__ import annotations

import pytest

from mini_gpt.ru_judge_evaluation import (
    V5ConversationCase,
    V5_CONVERSATION_SUITE,
    V5_PROMPT_ROUTING_SMOKE_SUITE,
    V5_PROACTIVE_FLIRT_SUITE,
    V5_NEW_CASE_IDS,
)
from mini_gpt.ru_evaluation_cases import (
    V4_CONVERSATION_SUITE,
    PROMPT_ROUTING_SMOKE_SUITE,
    PROACTIVE_FLIRT_SUITE,
)


def test_v5_suite_contains_all_v4_and_new_cases() -> None:
    v4_ids = {case.case_id for case in V4_CONVERSATION_SUITE}
    v5_ids = [case.case_id for case in V5_CONVERSATION_SUITE]
    assert v4_ids.issubset(set(v5_ids))
    assert len(v5_ids) == len(set(v5_ids))
    assert V5_NEW_CASE_IDS == (
        "vi_identity",
        "vi_follow_up_context",
        "en_identity",
        "en_follow_up_context",
        "mixed_routing",
    )
    for case_id in V5_NEW_CASE_IDS:
        assert case_id in v5_ids


def test_v5_case_structure() -> None:
    for case in V5_CONVERSATION_SUITE:
        assert isinstance(case, V5ConversationCase)
        assert case.language in {"vi", "en", "mixed"}
        assert case.messages[0][0] == "system"
        assert case.messages[-1][0] == "user"
        assert case.prompt == case.messages[-1][1]
        assert case.expected_topic is None or isinstance(case.expected_topic, str)


def test_v5_identity_and_routing_cases_are_in_character() -> None:
    by_id = {case.case_id: case for case in V5_CONVERSATION_SUITE}
    identity = by_id["vi_identity"]
    assert "trợ lý" not in identity.prompt.casefold()
    assert "ai model" not in identity.prompt.casefold()
    routing = by_id["mixed_routing"]
    assert routing.language == "mixed"
    assert len(routing.messages) == 4


def test_v5_reuses_existing_suites() -> None:
    assert V5_PROMPT_ROUTING_SMOKE_SUITE == PROMPT_ROUTING_SMOKE_SUITE
    assert V5_PROACTIVE_FLIRT_SUITE == PROACTIVE_FLIRT_SUITE

from mini_gpt.ru_judge_evaluation import assess_deterministic, V5_CONVERSATION_SUITE

_VI = {case.case_id: case for case in V5_CONVERSATION_SUITE}["vi_greeting"]
_EN = {case.case_id: case for case in V5_CONVERSATION_SUITE}["en_greeting"]


def test_disqualifier_empty_and_echo() -> None:
    assert "empty" in assess_deterministic(_VI, "").disqualifiers
    assert "echo" in assess_deterministic(_VI, "Xin chào em.").disqualifiers


def test_disqualifier_ai_disclaimer() -> None:
    result = assess_deterministic(_VI, "Em là trợ lý ảo, nhưng em sẽ lắng nghe anh.")
    assert "ai_disclaimer" in result.disqualifiers


def test_disqualifier_self_repetition() -> None:
    result = assess_deterministic(_VI, "Anh mệt rồi. Anh mệt rồi. Anh mệt rồi.")
    assert "self_repetition" in result.disqualifiers


def test_disqualifier_vts_and_voice_pace_leak() -> None:
    assert "vts_marker_leak" in assess_deterministic(_VI, "Em thương anh. [[vts: smile]]").disqualifiers
    assert "voice_pace_leak" in assess_deterministic(_VI, "Em thương anh [[VOICE_PACE:normal]]").disqualifiers
    assert "vts_marker_leak" in assess_deterministic(_VI, "Em tìm trên web [search: du lịch]").disqualifiers


def test_disqualifier_forbidden_pronouns_vi() -> None:
    result = assess_deterministic(_VI, "Tôi hiểu anh, tôi sẽ lắng nghe.")
    assert "forbidden_self_reference" in result.disqualifiers
    result = assess_deterministic(_VI, "Em hiểu bạn đang mệt.")
    assert "wrong_user_address" in result.disqualifiers


def test_disqualifier_language_mismatch() -> None:
    assert "vietnamese_voice" in assess_deterministic(_EN, "Hi anh, em đây nhé.").disqualifiers
    result = assess_deterministic(_VI, "I feel better now.")
    assert "no_language_voice" in result.disqualifiers


def test_language_score() -> None:
    assert assess_deterministic(_VI, "Em hiểu anh đang mệt.").language_score == 10
    assert assess_deterministic(_VI, "Em thấy anh mệt, take a rest nhé.").language_score == 6
    assert assess_deterministic(_EN, "I am here with you.").language_score == 10
    assert assess_deterministic(_EN, "Em ở đây với anh.").language_score == 0
    mixed = {case.case_id: case for case in V5_CONVERSATION_SUITE}["mixed_better"]
    assert assess_deterministic(mixed, "Em vui vì anh feels better today.").language_score == 10
    assert assess_deterministic(mixed, "Em vui vì anh khỏe hơn hôm nay.").language_score == 6


def test_voice_score() -> None:
    assert assess_deterministic(_VI, "Em hiểu anh đang mệt.").voice_score == 10
    assert assess_deterministic(_VI, "Em hiểu anh, mình sẽ ổn.").voice_score == 0
    assert assess_deterministic(_EN, "I am here with you.").voice_score == 10
    assert assess_deterministic(_EN, "I am here, anh nhé.").voice_score == 0


def test_hard_rules_score_deducts_emoji_and_lists() -> None:
    assert assess_deterministic(_VI, "Em thương anh.").hard_rules_score == 10
    result = assess_deterministic(_VI, "Em thương anh 😊😊.")
    assert result.hard_rules_score == 6
    result = assess_deterministic(_VI, "Em thấy:\n- anh mệt\n- anh cần nghỉ")
    assert result.hard_rules_score == 8

import json

from mini_gpt.ru_judge_evaluation import (
    JudgeScorer,
    JudgeResult,
    build_judge_messages,
    parse_judge_scores,
    V5_CONVERSATION_SUITE,
)

_VI = {case.case_id: case for case in V5_CONVERSATION_SUITE}["vi_greeting"]
_EN = {case.case_id: case for case in V5_CONVERSATION_SUITE}["en_identity"]


def _fake_generate(returns: list[str]) -> object:
    calls: list[tuple] = []

    def generate_fn(messages, max_new_tokens, temperature, top_k):
        calls.append((messages, max_new_tokens, temperature, top_k))
        return returns.pop(0) if returns else "{}"

    generate_fn.calls = calls  # type: ignore[attr-defined]
    return generate_fn


def test_parse_judge_scores_clean_json() -> None:
    scores = parse_judge_scores(
        '{"language": 9, "voice": 8, "naturalness": 7, "relevance": 6, "hard_rules": 10, "emotion": 8, "notes": "ok"}'
    )
    assert scores is not None
    assert scores["language"] == 9
    assert scores["emotion"] == 8


def test_parse_judge_scores_fenced_json() -> None:
    text = '```json\n{"language": 10, "voice": 10, "naturalness": 5, "relevance": 5, "hard_rules": 10, "emotion": 5}\n```'
    scores = parse_judge_scores(text)
    assert scores is not None
    assert scores["naturalness"] == 5


def test_parse_judge_scores_clamps_and_rejects() -> None:
    scores = parse_judge_scores(
        '{"language": 15, "voice": -3, "naturalness": 7.5, "relevance": "high", "hard_rules": 10, "emotion": 8}'
    )
    assert scores is None
    assert parse_judge_scores("not json at all") is None
    assert parse_judge_scores("") is None


def test_build_judge_messages_contains_context() -> None:
    (system_role, system), (user_role, user) = build_judge_messages(_VI, "Em chào anh!")
    assert system_role == "system" and user_role == "user"
    assert "anh" in user
    assert "Em chào anh!" in user
    assert "Xin chào em." in user
    assert "0" in system and "10" in system


def test_build_judge_messages_mentions_language_and_topic() -> None:
    (_, system), (_, user) = build_judge_messages(_EN, "I am Ru, your girlfriend.")
    assert "English" in system
    assert "identity" in system.casefold()


def test_judge_scorer_success_and_retry() -> None:
    fake = _fake_generate(["not json", '{"language": 8, "voice": 8, "naturalness": 7, "relevance": 7, "hard_rules": 10, "emotion": 7}'])
    scorer = JudgeScorer(generate_fn=fake)  # type: ignore[arg-type]
    result = scorer.score_case(_VI, "Em chào anh!")
    assert result.error is None
    assert result.scores is not None
    assert result.scores["language"] == 8
    assert len(fake.calls) == 2


def test_judge_scorer_returns_error_after_retry() -> None:
    fake = _fake_generate(["garbage", "more garbage"])
    scorer = JudgeScorer(generate_fn=fake)  # type: ignore[arg-type]
    result = scorer.score_case(_VI, "Em chào anh!")
    assert result.scores is None
    assert result.error is not None

import pytest as _pytest

from mini_gpt.ru_judge_evaluation import (
    CaseResult,
    ModelReport,
    V5_CONVERSATION_SUITE,
    run_suite,
    score_case,
    weighted_total,
)
from mini_gpt.ru_judge_evaluation import JudgeScorer

_WEIGHTS = {"language": 20, "voice": 20, "naturalness": 20, "relevance": 20, "hard_rules": 10, "emotion": 10}


def test_weighted_total() -> None:
    dims = {"language": 10, "voice": 10, "naturalness": 8, "relevance": 7, "hard_rules": 10, "emotion": 9}
    assert weighted_total(dims, _WEIGHTS) == _pytest.approx(89.0)
    partial = {"language": 10, "voice": 10, "hard_rules": 10}
    assert weighted_total(partial, _WEIGHTS) == _pytest.approx(100.0)


def test_score_case_passes_with_judge() -> None:
    result = score_case(
        _VI,
        "Em chào anh! Anh hôm nay thế nào?",
        _WEIGHTS,
        pass_threshold=70.0,
        disqualifiers=(),
        deterministic_scores={"language": 10, "voice": 10, "hard_rules": 10},
        judge_scores={"language": 9, "voice": 8, "naturalness": 8, "relevance": 8, "hard_rules": 10, "emotion": 8},
        judge_error=None,
    )
    assert result.passed
    assert result.total > 70
    assert result.dimensions["language"] == 10
    assert result.dimensions["naturalness"] == 8


def test_score_case_hard_fail_without_judge() -> None:
    result = score_case(
        _VI,
        "Em là trợ lý ảo.",
        _WEIGHTS,
        pass_threshold=70.0,
        disqualifiers=("ai_disclaimer",),
        deterministic_scores={"language": 0, "voice": 0, "hard_rules": 0},
        judge_scores=None,
        judge_error="judge call failed",
    )
    assert not result.passed
    assert "ai_disclaimer" in result.disqualifiers


def test_score_case_judge_error_falls_back_to_deterministic() -> None:
    result = score_case(
        _VI,
        "Em chào anh!",
        _WEIGHTS,
        pass_threshold=70.0,
        disqualifiers=(),
        deterministic_scores={"language": 10, "voice": 10, "hard_rules": 10},
        judge_scores=None,
        judge_error="judge call failed",
    )
    assert result.total == 100.0
    assert result.judge_error == "judge call failed"
    assert result.dimensions == {"language": 10.0, "voice": 10.0, "hard_rules": 10.0}


def test_run_suite_skips_judge_on_disqualifier() -> None:
    judge = JudgeScorer(generate_fn=lambda *a, **k: (_ for _ in ()).throw(AssertionError("judge must not be called")))

    def generate(case):
        return "Em là trợ lý ảo."

    results = run_suite(V5_CONVERSATION_SUITE[:2], generate, {}, judge, _WEIGHTS, pass_threshold=70.0)
    assert results[0].disqualifiers == ("ai_disclaimer",)
    assert results[0].judge_error is None


def test_run_suite_uses_judge_when_clean() -> None:
    judge = JudgeScorer(
        generate_fn=lambda messages, max_new_tokens, temperature, top_k: (
            '{"language": 9, "voice": 9, "naturalness": 8, "relevance": 8, "hard_rules": 10, "emotion": 8}'
        )
    )

    def generate(case):
        return "Em chào anh!"

    results = run_suite(V5_CONVERSATION_SUITE[:2], generate, {}, judge, _WEIGHTS, pass_threshold=70.0)
    assert all(result.passed for result in results)
    assert all(result.judge_error is None for result in results)


def test_model_report_computed_fields() -> None:
    results = (
        CaseResult(case_id="a", response="x", disqualifiers=(), dimensions={"language": 10, "voice": 10, "naturalness": 8, "relevance": 8, "hard_rules": 10, "emotion": 8}, total=90.0, passed=True, judge_error=None, generation_error=None),
        CaseResult(case_id="b", response="y", disqualifiers=("empty",), dimensions={"language": 0, "voice": 0, "naturalness": 0, "relevance": 0, "hard_rules": 0, "emotion": 0}, total=0.0, passed=False, judge_error="judge call failed", generation_error=None),
    )
    report = ModelReport(model_id="v1", adapter="lora.gguf", suite="v5", case_results=results)
    assert report.total == _pytest.approx(45.0)
    assert report.pass_rate == _pytest.approx(0.5)
    assert report.disqualifier_count == 1
    assert report.judge_error_count == 1
    assert report.dimension_averages["language"] == _pytest.approx(5.0)

import pytest as _pytest

from mini_gpt.ru_judge_evaluation import ModelReport, compare_models


def _report(total: float, pass_rate: float, dims: dict[str, float]) -> ModelReport:
    return ModelReport(
        model_id="m",
        adapter=None,
        suite="v5",
        case_results=(),
        _total_override=total,
        _pass_rate_override=pass_rate,
        _dims_override=dims,
    )


def test_compare_models_all_pass() -> None:
    baseline = _report(80.0, 0.9, {"language": 8.0, "voice": 8.0})
    candidate = _report(85.0, 0.95, {"language": 8.5, "voice": 8.5})
    result = compare_models(baseline, candidate)
    assert result.passed
    assert result.failure_reasons == ()
    assert result.total_delta == _pytest.approx(5.0)


def test_compare_models_fails_on_pass_rate() -> None:
    baseline = _report(80.0, 0.9, {"language": 8.0})
    candidate = _report(90.0, 0.7, {"language": 9.0})
    result = compare_models(baseline, candidate)
    assert not result.passed
    assert "pass_rate_below_threshold" in result.failure_reasons


def test_compare_models_fails_on_delta() -> None:
    baseline = _report(80.0, 0.9, {"language": 8.0})
    candidate = _report(70.0, 0.9, {"language": 7.0})
    result = compare_models(baseline, candidate)
    assert not result.passed
    assert "below_baseline" in result.failure_reasons
    assert result.dimension_deltas["language"] == _pytest.approx(-1.0)

import json

from mini_gpt.ru_judge_evaluation import (
    V5_CONVERSATION_SUITE,
    V5_PROMPT_ROUTING_SMOKE_SUITE,
    V5_PROACTIVE_FLIRT_SUITE,
    load_config,
    resolve_suite,
)

_CONFIG_JSON = json.dumps({
    "weights": {"language": 20, "voice": 20, "naturalness": 20, "relevance": 20, "hard_rules": 10, "emotion": 10},
    "pass_threshold": 70.0,
    "min_pass_rate": 0.8,
    "baseline_delta_tolerance": 5.0,
    "judge": {"model": "kilo-auto/free", "temperature": 0.2, "max_tokens": 400},
    "generation": {"max_new_tokens": 180, "temperature": 0.7, "top_k": 50},
    "models": {"base": {"adapter": None}, "v1": {"adapter": "models/gguf/ru-lfm2.5-2.6b-v1-lora.gguf"}},
})


def test_load_config(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(_CONFIG_JSON, encoding="utf-8")
    config = load_config(path)
    assert config["pass_threshold"] == 70.0
    assert config["models"]["v1"]["adapter"] == "models/gguf/ru-lfm2.5-2.6b-v1-lora.gguf"


def test_resolve_suite_names() -> None:
    assert resolve_suite("v5") == V5_CONVERSATION_SUITE
    assert resolve_suite("smoke") == V5_PROMPT_ROUTING_SMOKE_SUITE
    assert resolve_suite("flirt") == V5_PROACTIVE_FLIRT_SUITE


def test_resolve_suite_rejects_unknown() -> None:
    with _pytest.raises(ValueError):
        resolve_suite("nope")

from mini_gpt.ru_judge_evaluation import build_judge_messages, V5_PROMPT_ROUTING_SMOKE_SUITE


def test_build_judge_messages_accepts_plain_conversation_cases() -> None:
    case = V5_PROMPT_ROUTING_SMOKE_SUITE[0]
    (_, system), (_, user) = build_judge_messages(case, "Em chào anh!")
    assert "general warm companionship chat" in system
    assert "Em chào anh!" in user
