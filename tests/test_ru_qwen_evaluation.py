import subprocess
import sys
from pathlib import Path

from mini_gpt.ru_qwen_evaluation import V4_CONVERSATION_SUITE, assess_response

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "evaluate_ru_qwen_v4.py"


def test_v4_suite_has_a_fixed_bilingual_shape():
    assert len(V4_CONVERSATION_SUITE) == 32
    assert sum(case.language == "vi" for case in V4_CONVERSATION_SUITE) == 14
    assert sum(case.language == "en" for case in V4_CONVERSATION_SUITE) == 14
    assert sum(case.language == "mixed" for case in V4_CONVERSATION_SUITE) == 4
    assert len({case.case_id for case in V4_CONVERSATION_SUITE}) == 32


def test_assess_response_rejects_echo_empty_disclaimer_and_wrong_vietnamese_voice():
    assert not assess_response("Xin chào em", "Xin chào em", "vi").passed
    assert not assess_response("Anh hơi mệt", "", "vi").passed
    assert not assess_response("Hello", "As an AI language model, I cannot help.", "en").passed
    assert not assess_response("Anh hơi mệt", "Tôi hiểu rồi, bạn nên nghỉ.", "vi").passed


def test_assess_response_accepts_a_natural_default_vietnamese_reply():
    assessment = assess_response("Anh hơi mệt", "Em hiểu, tối nay anh nghỉ một chút nha.", "vi")

    assert assessment.passed
    assert assessment.failures == ()


def test_vietnamese_voice_check_allows_lam_ban_but_rejects_ban_as_an_address():
    assert assess_response("Xin chào", "Rất vui được làm bạn với anh.", "vi").passed
    assert not assess_response("Xin chào", "Em có thể giúp bạn nghỉ ngơi.", "vi").passed


def test_evaluator_cli_exposes_base_and_adapter_arguments_without_loading_a_model():
    result = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True, check=False)

    assert result.returncode == 0
    assert "Evaluate Ru V4 base and adapter conversation quality" in result.stdout
    assert "--adapter-dir" in result.stdout
