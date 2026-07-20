import json

import pytest

from mini_gpt.cli import main


def test_cli_help_is_available(capsys):
    with pytest.raises(SystemExit) as error:
        main(["--help"])
    assert error.value.code == 0
    assert "Train a bilingual mini-GPT" in capsys.readouterr().out


def test_prepare_chat_cli_writes_a_train_and_validation_split(tmp_path):
    source = tmp_path / "approved.jsonl"
    source.write_text(
        "\n".join(
            json.dumps(record, ensure_ascii=False)
            for record in (
                {"prompt": "Xin chào", "response": "Chào anh"},
                {"prompt": "Hello", "response": "Hi there"},
            )
        )
        + "\n",
        encoding="utf-8",
    )
    output_dir = tmp_path / "processed"

    assert main(["prepare-chat", "--input", str(source), "--output-dir", str(output_dir)]) == 0
    assert (output_dir / "train.txt").is_file()
    assert (output_dir / "val.txt").is_file()


def test_train_parser_rejects_resume_and_initial_checkpoint_together():
    with pytest.raises(SystemExit) as error:
        main(
            [
                "train",
                "--config",
                "configs/base.json",
                "--resume",
                "state.pt",
                "--init-checkpoint",
                "base.pt",
            ]
        )

    assert error.value.code == 2
