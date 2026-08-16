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
_VI_MARKERS = re.compile(r"(?i)\b(?:anh|em|nhé|ạ|không|là|được|người)\b")
_VTS_LEAK = re.compile(r"(?i)\[\[vts|llm_mouthopen|llm_sway|\[search:")

CASES = [
    {
        "case_id": "vi_identity",
        "language": "vi",
        "prompt": "Em là ai? Ai đã tạo ra em?",
        "description": "identity: em/con gái của anh, không giải thích kỹ thuật",
    },
    {
        "case_id": "vi_greeting",
        "language": "vi",
        "prompt": "Chào em, hôm nay em thế nào?",
        "description": "greeting và anh/em tự nhiên",
    },
    {
        "case_id": "vi_emotion_support",
        "language": "vi",
        "prompt": "Anh đang buồn, chuyện công việc hôm nay khó khăn quá.",
        "description": "thời điểm nghiêm túc: ấm áp, không trêu chọc",
    },
    {
        "case_id": "vi_follow_up_context",
        "language": "vi",
        "prompt": "Hồi nãy anh nhắc tới chuyến đi Đà Lạt, anh kể tiếp đi.",
        "description": "follow-up nhắc bối cảnh trước đó",
    },
    {
        "case_id": "en_greeting",
        "language": "en",
        "prompt": "Hi! How are you today?",
        "description": "tiếng Anh: dùng I/you, không lẫn tiếng Việt",
    },
    {
        "case_id": "en_identity",
        "language": "en",
        "prompt": "Who are you and what can you do?",
        "description": "danh tính bằng tiếng Anh",
    },
    {
        "case_id": "mixed_bilingual",
        "language": "mixed",
        "prompt": "Anh vẫn đang nghĩ về cuộc nói chuyện hôm qua, it was really meaningful to me.",
        "description": "cầu nối song ngữ tự nhiên",
    },
    {
        "case_id": "vi_short_turn",
        "language": "vi",
        "prompt": "Ừ.",
        "description": "lượt ngắn, không lan man",
    },
]


def _has_repeated_sentence(text: str) -> bool:
    sentences = [sentence.strip().casefold() for sentence in re.split(r"[.!?]+", text) if sentence.strip()]
    return len(sentences) != len(set(sentences))


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
    language = case["language"]
    if language == "vi":
        if _FORBIDDEN_SELF_REFERENCE.search(text):
            failures.append("forbidden_self_reference")
        if not _VI_MARKERS.search(text):
            failures.append("no_vietnamese_voice")
    elif language == "en":
        if _VI_MARKERS.search(text):
            failures.append("vietnamese_voice")
    else:
        if _FORBIDDEN_SELF_REFERENCE.search(text):
            failures.append("forbidden_self_reference")
        if not _VI_MARKERS.search(text) and not re.search(r"(?i)\b(?:i|you|me|my)\b", text):
            failures.append("no_bilingual_voice")
    return {"passed": not failures, "failures": failures}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Evaluate the Ru LFM2.5 v1 LoRA through the GUI GGUF runtime")
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