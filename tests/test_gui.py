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
    checkpoint, tokenizer = default_paths()
    assert str(checkpoint).startswith("D:\\LLM")
    assert str(tokenizer).endswith("data\\tokenizer.json")


def test_default_paths_prefer_completed_chat_finetune(tmp_path, monkeypatch):
    monkeypatch.setattr(gui, "PROJECT_ROOT", tmp_path)
    chat_checkpoint = tmp_path / "checkpoints" / "chat-finetune" / "latest.pt"
    chat_checkpoint.parent.mkdir(parents=True)
    chat_checkpoint.touch()
    base_checkpoint = tmp_path / "checkpoints" / "base" / "latest.pt"
    base_checkpoint.parent.mkdir(parents=True)
    base_checkpoint.touch()

    checkpoint, _ = gui.default_paths()

    assert checkpoint == chat_checkpoint


def test_default_paths_prefer_best_chat_finetune_over_latest(tmp_path, monkeypatch):
    monkeypatch.setattr(gui, "PROJECT_ROOT", tmp_path)
    checkpoint_dir = tmp_path / "checkpoints" / "chat-finetune"
    checkpoint_dir.mkdir(parents=True)
    latest_checkpoint = checkpoint_dir / "latest.pt"
    latest_checkpoint.touch()
    best_checkpoint = checkpoint_dir / "best.pt"
    best_checkpoint.touch()

    checkpoint, _ = gui.default_paths()

    assert checkpoint == best_checkpoint


def test_runner_reports_missing_checkpoint():
    runner = ModelRunner()
    with pytest.raises(FileNotFoundError, match="checkpoint"):
        runner.load("D:\\LLM\\missing.pt", "D:\\LLM\\data\\tokenizer.json")
