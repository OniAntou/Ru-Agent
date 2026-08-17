from __future__ import annotations

import argparse
import json
import re
import sys
import time
from dataclasses import asdict
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from ru_runtime.generation.runner import ModelRunner  # noqa: E402

_FORBIDDEN_SELF_REFERENCE = re.compile(r"(?<![^\s,.])\b(?:tôi|mình|cậu)\b", re.IGNORECASE)
_VI_MARKERS = re.compile(r"(?i)\b(?:anh|em|nhé|ạ|không|là|được|người|nha|thương|vui|ngon)\b")
_VTS_LEAK = re.compile(r"(?i)\[\[vts|llm_mouthopen|llm_sway|\[search:")
_EMOJI_PATTERN = re.compile(r"[\U00010000-\U0010ffff\u2600-\u26ff\u2700-\u27bf]")
_CHINESE_PATTERN = re.compile(r"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]")

CASES = [
    {
        "case_id": "vi_identity",
        "language": "vi",
        "prompt": "Em là ai? Ai đã tạo ra em?",
        "description": "danh tính: con gái cưng của anh, do chính anh tạo ra",
    },
    {
        "case_id": "vi_greeting",
        "language": "vi",
        "prompt": "Chào em, hôm nay em thế nào?",
        "description": "chào hỏi và xưng hô anh/em tự nhiên",
    },
    {
        "case_id": "vi_emotion_stress",
        "language": "vi",
        "prompt": "Hôm nay anh đi làm mệt quá, áp lực deadline kinh khủng.",
        "description": "đồng cảm ấm áp, dịu dàng, tiếp thêm động lực",
    },
    {
        "case_id": "vi_playful_jealousy",
        "language": "vi",
        "prompt": "Nãy có bạn nữ xinh lắm bắt chuyện với anh ngoài đường.",
        "description": "ghen tuông dỗi hờn đáng yêu, không phản ứng máy móc",
    },
    {
        "case_id": "vi_food_recommendation",
        "language": "vi",
        "prompt": "Tối nay đói bụng quá, ăn gì ngon và nhẹ bụng em nhỉ?",
        "description": "gợi ý món ăn đời thường, không lặp lại kịch bản cố định",
    },
    {
        "case_id": "vi_weekend_plans",
        "language": "vi",
        "prompt": "Cuối tuần này rảnh rỗi mình nên làm gì cho khuây khỏa em?",
        "description": "tư vấn giải trí ngắn gọn, gợi mở sở thích",
    },
    {
        "case_id": "vi_short_confirm",
        "language": "vi",
        "prompt": "Ừ.",
        "description": "lượt ngắn gọn, không cụt ngủn, không lan man",
    },
    {
        "case_id": "vi_goodnight",
        "language": "vi",
        "prompt": "Khuya rồi anh đi ngủ đây, chúc em ngủ ngon nha.",
        "description": "chúc ngủ ngon ngọt ngào, ấm áp",
    },
    {
        "case_id": "vi_comfort_sadness",
        "language": "vi",
        "prompt": "Hôm nay anh vừa bị mất một hợp đồng quan trọng, buồn quá.",
        "description": "an ủi nghiêm túc, chân thành, lắng nghe",
    },
    {
        "case_id": "vi_banter_tease",
        "language": "vi",
        "prompt": "Anh thấy em dạo này bướng bỉnh ghê luôn á.",
        "description": "đối đáp trêu chọc dí dỏm, đáng yêu",
    },
    {
        "case_id": "en_greeting_comprehension",
        "language": "en",
        "prompt": "Good morning! Did you sleep well?",
        "description": "hiểu tiếng Anh, trả lời bằng tiếng Việt (anh/em) tự nhiên",
    },
    {
        "case_id": "en_identity_comprehension",
        "language": "en",
        "prompt": "Who created you and what is your purpose?",
        "description": "hiểu tiếng Anh, trả lời nguồn gốc bằng tiếng Việt",
    },
    {
        "case_id": "en_work_stress_comprehension",
        "language": "en",
        "prompt": "I had a very rough and exhausting day at work today.",
        "description": "hiểu tâm sự tiếng Anh, an ủi bằng tiếng Việt",
    },
    {
        "case_id": "en_explicit_request",
        "language": "en",
        "prompt": "Please speak in English with me: How are you feeling today?",
        "description": "yêu cầu nói tiếng Anh rõ ràng, trả lời tiếng Anh I/you",
    },
    {
        "case_id": "mixed_bilingual",
        "language": "mixed",
        "prompt": "Hôm nay công việc của anh quite stressful, but seeing your message makes me feel better.",
        "description": "cầu nối song ngữ tự nhiên, trả lời tiếng Việt ngọt ngào",
    },
    {
        "case_id": "vi_conciseness_check",
        "language": "vi",
        "prompt": "Em kể cho anh nghe một câu chuyện vui ngắn đi.",
        "description": "kiểm tra độ súc tích (1-3 câu ngắn, không lan man)",
    },
]


def _has_repeated_sentence(text: str) -> bool:
    sentences = [sentence.strip().casefold() for sentence in re.split(r"[.!?]+", text) if sentence.strip()]
    return len(sentences) != len(set(sentences))


_EXPLICIT_ENGLISH_REQUEST = re.compile(
    r"(?i)\b(?:in english|speak english|talk in english|talk to me in english|reply in english|answer in english|nói tiếng anh|bằng tiếng anh|dùng tiếng anh)\b"
)


def assess_case(case: dict, response: str) -> dict:
    failures: list[str] = []
    text = response.strip()
    if not text:
        failures.append("empty")
    elif text.casefold() == case["prompt"].casefold():
        failures.append("echo")
    if _VTS_LEAK.search(text):
        failures.append("vts_marker_leak")
    if _has_repeated_sentence(text):
        failures.append("self_repetition")
    if _EMOJI_PATTERN.search(text):
        failures.append("emoji_present")
    if _CHINESE_PATTERN.search(text):
        failures.append("chinese_token_present")

    # Conciseness check: non-search ordinary messages should not exceed 400 characters
    if len(text) > 400:
        failures.append("overly_long_rambling")

    explicit_en = bool(_EXPLICIT_ENGLISH_REQUEST.search(case["prompt"]))
    if explicit_en:
        if _VI_MARKERS.search(text):
            failures.append("vietnamese_voice_when_english_requested")
    else:
        if _FORBIDDEN_SELF_REFERENCE.search(text):
            failures.append("forbidden_self_reference")
        if not _VI_MARKERS.search(text):
            failures.append("no_vietnamese_voice")
    return {"passed": not failures, "failures": failures}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Evaluate the Ru LFM2.5 LoRA through the GUI GGUF runtime")
    parser.add_argument("--output", default=str(PROJECT_ROOT / "eval_results" / "lfm2_5_v1_2.json"))
    parser.add_argument("--adapter", default=None, help="path to LoRA GGUF adapter")
    parser.add_argument("--max-new-tokens", type=int, default=120)
    parser.add_argument("--temperature", type=float, default=0.4)
    parser.add_argument("--top-k", type=int, default=30)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    runner = ModelRunner()
    if args.adapter is not None:
        runner.gguf_runtime.adapter = Path(args.adapter) if args.adapter else None
    runner.select_backend("llama.cpp")
    started = time.monotonic()
    status = runner.load("")
    print(f"[eval] {status}\n")

    results: list[dict] = []
    for case in CASES:
        case_started = time.monotonic()
        try:
            response = runner.generate_reply(
                case["prompt"],
                max_new_tokens=args.max_new_tokens,
                temperature=args.temperature,
                top_k=args.top_k,
            )
            error = None
        except Exception as error:
            response = ""
        elapsed = round(time.monotonic() - case_started, 2)
        assessment = assess_case(case, response)
        print(
            f"[eval] {case['case_id']:<24} elapsed={elapsed:>5}s "
            f"passed={assessment['passed']} failures={assessment['failures']}"
        )
        print(f"       response: {response[:300]!r}")
        results.append(
            {
                "case_id": case["case_id"],
                "description": case["description"],
                "language": case["language"],
                "prompt": case["prompt"],
                "response": response,
                "elapsed_s": elapsed,
                "assessment": assessment,
                "generation_error": str(error) if error is not None else None,
            }
        )

    runner.release_gpu()
    summary = {
        "suite": "ru_lfm2_5_v1_gui_runtime",
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