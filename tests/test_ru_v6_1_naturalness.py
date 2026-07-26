import json
import subprocess
import sys
from pathlib import Path

from mini_gpt.ru_v6_1_naturalness import audit_rows, naturalness_rejection_reason


def _row(prompt: str, response: str, **metadata: str) -> dict[str, object]:
    return {
        "messages": [
            {"role": "system", "content": "Ru"},
            {"role": "user", "content": prompt},
        ],
        "response": response,
        **metadata,
    }


def test_rejects_ai_meta_language():
    assert (
        naturalness_rejection_reason(
            "Anh thấy sao?",
            "As an AI language model, em không biết.",
        )
        == "ai_meta_language"
    )


def test_rejects_mechanical_punctuation_and_address_duplication():
    assert naturalness_rejection_reason("Anh về rồi.", "Em nghe anh đây..") == "repeated_punctuation"
    assert naturalness_rejection_reason("Anh về rồi.", "Anh anh cứ nghỉ một chút.") == "duplicated_address"


def test_audit_reports_casefold_prompt_collision_and_repeated_response():
    report = audit_rows(
        [
            _row("Chào em", "Chào anh!"),
            _row("chào em", "Chào anh!"),
        ]
    )

    assert report["summary"]["casefold_prompt_collisions"] == ["chào em"]
    assert report["summary"]["repeated_response_count"] == 1
    assert report["summary"]["rejection_count"] == 0
    assert report["summary"]["largest_response_opening_count"] == 2


def test_audit_accepts_short_everyday_anh_em_reply():
    assert naturalness_rejection_reason("Anh mới về.", "Về rồi à anh, nghỉ một chút đã nhé.") is None


def test_naturalness_cli_writes_full_corpus_report(tmp_path):
    output = tmp_path / "audit.json"

    subprocess.run(
        [sys.executable, "scripts/audit_ru_v6_1_naturalness.py", "--output", str(output)],
        check=True,
    )

    report = json.loads(output.read_text(encoding="utf-8"))
    assert report["summary"]["row_count"] == 2400
    assert report["summary"]["rejection_count"] == 0
    assert report["summary"]["repeated_response_count"] == 0
