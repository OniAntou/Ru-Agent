import pytest

from mini_gpt.cli import main


def test_cli_help_is_available(capsys):
    with pytest.raises(SystemExit) as error:
        main(["--help"])
    assert error.value.code == 0
    assert "Train a bilingual mini-GPT" in capsys.readouterr().out

