from __future__ import annotations

from ru_runtime.evaluation.cases import (
    PROMPT_ROUTING_SMOKE_SUITE,
    PROACTIVE_FLIRT_SUITE,
    V4_CONVERSATION_SUITE,
    assess_response,
)


def test_neutral_evaluation_module_preserves_public_case_suites() -> None:
    assert len(V4_CONVERSATION_SUITE) == 32
    assert tuple(case.case_id for case in PROMPT_ROUTING_SMOKE_SUITE) == (
        "vi_greeting",
        "vi_context_work",
        "en_greeting",
        "en_tired",
        "en_context_work",
        "en_small_talk",
        "mixed_better",
        "mixed_context",
    )
    assert len(PROACTIVE_FLIRT_SUITE) == 12


def test_neutral_evaluation_module_preserves_response_gate() -> None:
    assert assess_response("Xin chào em.", "Em chào anh.", "vi").passed
    # English prompt without explicit English request expects Vietnamese response
    assert assess_response("Hi Ru.", "Hi anh, em đây nhé.", "en").passed
    # Explicit request for English expects English response, fails if Vietnamese is used
    assert assess_response("Please talk to me in English.", "Hi anh, em đây nhé.", "en").failures == (
        "vietnamese_voice",
    )
