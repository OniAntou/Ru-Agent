import pytest

from mini_gpt.gui import ModelRunner, default_paths, parse_generation_settings


def test_generation_settings_are_parsed_and_validated():
    assert parse_generation_settings("50", "0.8", "20") == (50, 0.8, 20)
    with pytest.raises(ValueError, match="temperature"):
        parse_generation_settings("50", "0", "20")
    with pytest.raises(ValueError, match="tokens"):
        parse_generation_settings("0", "0.8", "20")


def test_default_paths_point_inside_project():
    checkpoint, tokenizer = default_paths()
    assert str(checkpoint).startswith("D:\\LLM")
    assert str(tokenizer).endswith("data\\tokenizer.json")


def test_runner_reports_missing_checkpoint():
    runner = ModelRunner()
    with pytest.raises(FileNotFoundError, match="checkpoint"):
        runner.load("D:\\LLM\\missing.pt", "D:\\LLM\\data\\tokenizer.json")
