from __future__ import annotations

import json

import pytest

from ru_runtime.memory import MemoryKind
from ru_runtime.memory_learning import ExtractFact, extract_facts_with_llm


def _json_generator(payload: str):
    return lambda fact_prompt, max_new_tokens, temperature, top_k, allow_web_search: payload


def test_extracts_valid_facts() -> None:
    generator = _json_generator(
        json.dumps([{"kind": "preference", "content": "Anh thích cà phê đen", "confidence": 0.9, "importance": 0.6}])
    )
    facts = extract_facts_with_llm("anh thích cà phê", "Ru: Được ạ.", generator)
    assert len(facts) == 1
    assert facts[0].kind == MemoryKind.PREFERENCE
    assert facts[0].content == "Anh thích cà phê đen"
    assert facts[0].confidence == 0.9
    assert facts[0].importance == 0.6


def test_strips_code_fence() -> None:
    generator = _json_generator(
        '```json\n[{"kind": "project", "content": "Dùng D:\\\\LLM cho gui", "confidence": 1.0, "importance": 0.8}]\n```'
    )
    facts = extract_facts_with_llm("sẽ dùng D:\\LLM", "ok", generator)
    assert len(facts) == 1
    assert facts[0].kind == MemoryKind.PROJECT


def test_empty_json_returns_no_facts() -> None:
    generator = _json_generator("[]")
    assert extract_facts_with_llm("chào", "chào anh", generator) == ()


def test_invalid_json_returns_no_facts() -> None:
    generator = _json_generator("đây không phải json")
    assert extract_facts_with_llm("chào", "chào anh", generator) == ()


def test_generator_exception_returns_no_facts() -> None:
    def broken(*args, **kwargs):
        raise RuntimeError("network down")

    assert extract_facts_with_llm("chào", "chào anh", broken) == ()


def test_invalid_kind_is_dropped() -> None:
    generator = _json_generator(
        json.dumps([
            {"kind": "banana", "content": "x", "confidence": 0.5, "importance": 0.5},
            {"kind": "decision", "content": "y", "confidence": 0.5, "importance": 0.5},
        ])
    )
    facts = extract_facts_with_llm("chào", "chào", generator)
    assert len(facts) == 1
    assert facts[0].kind == MemoryKind.DECISION


def test_unhashable_kind_is_dropped() -> None:
    generator = _json_generator(
        json.dumps([
            {"kind": {"x": 1}, "content": "x", "confidence": 0.5, "importance": 0.5},
            {"kind": "decision", "content": "y", "confidence": 0.5, "importance": 0.5},
        ])
    )
    facts = extract_facts_with_llm("chào", "chào", generator)
    assert len(facts) == 1
    assert facts[0].kind == MemoryKind.DECISION


def test_non_finite_confidence_and_importance_are_dropped() -> None:
    generator = _json_generator(
        '[{"kind": "profile", "content": "nan fact", "confidence": NaN, "importance": 0.5}]'
    )
    assert extract_facts_with_llm("chào", "chào", generator) == ()
    generator = _json_generator(
        '[{"kind": "profile", "content": "inf fact", "confidence": 0.5, "importance": Infinity}]'
    )
    assert extract_facts_with_llm("chào", "chào", generator) == ()


def test_sensitive_content_is_dropped() -> None:
    generator = _json_generator(
        json.dumps([
            {"kind": "profile", "content": "api_key = sk-abc123", "confidence": 0.9, "importance": 0.9},
            {"kind": "profile", "content": "Anh tên là Minh", "confidence": 0.9, "importance": 0.7},
        ])
    )
    facts = extract_facts_with_llm("chào", "chào", generator)
    assert len(facts) == 1
    assert facts[0].content == "Anh tên là Minh"


def test_max_five_facts_and_content_truncated() -> None:
    many = [
        {"kind": "profile", "content": f"fact số {i} lặp lặp", "confidence": 0.5, "importance": 0.5}
        for i in range(7)
    ]
    long_content = "x" * 500
    many.insert(0, {"kind": "decision", "content": long_content, "confidence": 0.5, "importance": 0.5})
    generator = _json_generator(json.dumps(many))
    facts = extract_facts_with_llm("chào", "chào", generator)
    assert len(facts) <= 5
    assert all(len(fact.content) <= 300 for fact in facts)


def test_generator_called_with_extraction_settings() -> None:
    captured = {}

    def spy(fact_prompt, max_new_tokens, temperature, top_k, allow_web_search):
        captured.update(
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
            allow_web_search=allow_web_search,
            prompt=fact_prompt,
        )
        return "[]"

    extract_facts_with_llm("chào", "chào anh", spy)
    assert captured["max_new_tokens"] == 150
    assert captured["temperature"] == 0.2
    assert captured["top_k"] == 50
    assert captured["allow_web_search"] is False
    assert "chào anh" in captured["prompt"]
