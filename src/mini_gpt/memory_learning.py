from __future__ import annotations

import json
import logging
import re
from dataclasses import dataclass

from mini_gpt.memory import MemoryKind
from mini_gpt.memory_extraction import is_sensitive_or_unsafe

logger = logging.getLogger(__name__)

_MAX_FACTS = 5
_MAX_FACT_CHARS = 300
_EXTRACTION_MAX_TOKENS = 150
_EXTRACTION_TEMPERATURE = 0.2
_EXTRACTION_TOP_K = 50
_VALID_KINDS = frozenset(MemoryKind.__members__.values())
_CODE_FENCE_RE = re.compile(r"```(?:json)?\s*([\s\S]*?)```", re.IGNORECASE)

_EXTRACTION_SYSTEM_PROMPT = """Bạn là hệ thống trích xuất tri thức. Cho lượt trò chuyện sau, trích các fact rõ ràng, có giá trị lâu dài: sở thích, thông tin cá nhân, quyết định, thông tin dự án/đường dẫn.

BỎ QUA: thông tin nhạy cảm (API key, mật khẩu, token, số tài khoản, URL), nội dung nhất thời, trả lời chung chung, marker nội bộ [[VTS:...]].

Chỉ trả về JSON thuần, không giải thích, không markdown:
[{{"kind": "preference|profile|project|decision", "content": "...", "confidence": 0.0-1.0, "importance": 0.0-1.0}}]
Không có fact nào thì trả về []. Tối đa 5 fact, mỗi content tối đa 300 ký tự.

Hội thoại:
User: {prompt}
Assistant: {reply}"""


@dataclass(frozen=True, slots=True)
class ExtractFact:
    kind: MemoryKind
    content: str
    confidence: float
    importance: float


def _strip_markdown(json_text: str) -> str:
    fence = _CODE_FENCE_RE.search(json_text)
    return fence.group(1).strip() if fence else json_text.strip()


def _clean_content(content: str) -> str:
    content = re.sub(r"\[\[VTS:.*?\]\]", "", content, flags=re.IGNORECASE).strip()
    return content[: _MAX_FACT_CHARS].strip()


def extract_facts_with_llm(prompt: str, reply: str, generate_fn) -> tuple[ExtractFact, ...]:
    fact_prompt = _EXTRACTION_SYSTEM_PROMPT.format(prompt=prompt[:4000], reply=reply[:4000])
    try:
        raw = generate_fn(fact_prompt, _EXTRACTION_MAX_TOKENS, _EXTRACTION_TEMPERATURE, _EXTRACTION_TOP_K, False)
    except Exception as error:
        logger.warning("Fact extraction generation failed: %s", error)
        return ()
    try:
        payload = json.loads(_strip_markdown(raw))
    except (json.JSONDecodeError, TypeError):
        logger.debug("Fact extraction returned non-JSON: %.120s", raw)
        return ()
    if not isinstance(payload, list):
        return ()
    facts: list[ExtractFact] = []
    for item in payload[: _MAX_FACTS * 2]:
        if not isinstance(item, dict):
            continue
        kind = item.get("kind")
        if kind not in _VALID_KINDS:
            continue
        content = _clean_content(str(item.get("content", "")))
        if is_sensitive_or_unsafe(content):
            continue
        try:
            confidence = max(0.0, min(1.0, float(item.get("confidence", 0.5))))
            importance = max(0.0, min(1.0, float(item.get("importance", 0.5))))
        except (TypeError, ValueError):
            continue
        if not content:
            continue
        facts.append(ExtractFact(MemoryKind(kind), content, confidence, importance))
        if len(facts) >= _MAX_FACTS:
            break
    return tuple(facts)
