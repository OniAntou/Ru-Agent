import pytest

from mini_gpt import gui
from mini_gpt.gui import MODEL_NAME, ModelRunner, default_paths, parse_generation_settings


def test_model_name_is_ru():
    assert MODEL_NAME == "Ru"


def test_generation_settings_are_parsed_and_validated():
    assert parse_generation_settings("50", "0.8", "20") == (50, 0.8, 20)
    with pytest.raises(ValueError, match="temperature"):
        parse_generation_settings("50", "0", "20")
    with pytest.raises(ValueError, match="tokens"):
        parse_generation_settings("0", "0.8", "20")


def test_default_paths_point_inside_project():
    checkpoint = default_paths()
    assert str(checkpoint).startswith("D:\\LLM")


def test_default_paths_fall_back_to_last_candidate(tmp_path, monkeypatch):
    monkeypatch.setattr(gui, "PROJECT_ROOT", tmp_path)
    checkpoint = gui.default_paths()
    assert str(checkpoint).endswith("ru-llama3.1-8b-instruct-v5")


def test_runner_reports_missing_checkpoint():
    runner = ModelRunner()
    with pytest.raises(FileNotFoundError, match="adapter directory"):
        runner.load("D:\\LLM\\nonexistent")
