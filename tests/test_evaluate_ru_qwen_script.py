import importlib.util
import json
from pathlib import Path
import subprocess
import sys


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "evaluate_ru_qwen_v4.py"
V5_CONFIG = Path(__file__).resolve().parents[1] / "configs" / "ru_qwen_v5_3b.json"
QWEN3_CONFIG = Path(__file__).resolve().parents[1] / "configs" / "ru_qwen3_4b.json"
REVIEW_SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "record_ru_qwen_base_review.py"


def test_v5_base_only_config_loads_without_an_adapter_or_training_settings():
    spec = importlib.util.spec_from_file_location("evaluate_ru_qwen", SCRIPT)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)

    config = module.load_evaluation_config(V5_CONFIG)

    assert config["profile_name"] == "ru_qwen_v5_3b"
    assert config["phase"] == "base_only"
    assert config["base_model_id"] == "Qwen/Qwen2.5-3B-Instruct"
    assert "adapter_dir" not in config
    assert "dataset_dir" not in config


def test_evaluator_reports_cuda_memory_in_mebibytes():
    spec = importlib.util.spec_from_file_location("evaluate_ru_qwen_memory", SCRIPT)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)

    assert module.bytes_to_mib(5_500 * 1024 * 1024) == 5500.0


def test_qwen3_base_only_config_loads_without_an_adapter_or_training_settings():
    spec = importlib.util.spec_from_file_location("evaluate_ru_qwen_qwen3", SCRIPT)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)

    config = module.load_evaluation_config(QWEN3_CONFIG)

    assert config["profile_name"] == "ru_qwen3_4b"
    assert config["base_model_id"] == "Qwen/Qwen3-4B"
    assert config["base_model_path"] == "models/qwen3-4b"
    assert config["phase"] == "base_only"
    assert "adapter_dir" not in config
    assert "dataset_dir" not in config


def test_base_review_recorder_exposes_help_without_changing_an_evaluation():
    result = subprocess.run([sys.executable, str(REVIEW_SCRIPT), "--help"], capture_output=True, text=True, check=False)

    assert result.returncode == 0
    assert "Record a manual Ru base-only evaluation decision" in result.stdout


def test_base_review_recorder_writes_a_not_adopted_decision_for_an_automatic_failure(tmp_path):
    evaluation_path = tmp_path / "evaluation.json"
    review_path = tmp_path / "review.json"
    evaluation_path.write_text(
        json.dumps(
            {
                "profile_name": "ru_qwen_v5_3b",
                "cases": [
                    {
                        "case_id": "case_1",
                        "base_assessment": {"passed": False, "failures": ["echo"]},
                    }
                ],
            }
        ),
        encoding="utf-8",
    )
    review_path.write_text(
        json.dumps(
            {
                "reviewer_notes": "Echoed the prompt.",
                "reviews": [
                    {
                        "case_id": "case_1",
                        "natural": True,
                        "relevant": True,
                        "expected_language": True,
                        "voice": True,
                        "notes": "Manual review complete.",
                    }
                ],
            }
        ),
        encoding="utf-8",
    )

    result = subprocess.run(
        [sys.executable, str(REVIEW_SCRIPT), "--evaluation", str(evaluation_path), "--review", str(review_path)],
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode == 0
    persisted = json.loads(evaluation_path.read_text(encoding="utf-8"))
    assert persisted["verdict"] == "not_adopted"
    assert persisted["decision"]["failure_reasons"] == ["automatic_disqualifiers", "manual_score_below_required"]


def test_base_review_recorder_can_stop_at_an_automatic_failure_without_manual_reviews(tmp_path):
    evaluation_path = tmp_path / "evaluation.json"
    evaluation_path.write_text(
        json.dumps(
            {
                "profile_name": "ru_qwen_v5_3b",
                "cases": [{"case_id": "case_1", "base_assessment": {"passed": False, "failures": ["echo"]}}],
            }
        ),
        encoding="utf-8",
    )

    result = subprocess.run(
        [sys.executable, str(REVIEW_SCRIPT), "--evaluation", str(evaluation_path), "--automatic-only"],
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode == 0
    persisted = json.loads(evaluation_path.read_text(encoding="utf-8"))
    assert persisted["decision"]["manual_natural_count"] is None
    assert persisted["decision"]["failure_reasons"] == ["automatic_disqualifiers"]
