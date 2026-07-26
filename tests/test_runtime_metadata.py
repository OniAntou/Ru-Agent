import tomllib
from pathlib import Path


def test_project_metadata_declares_runtime_extra():
    config = tomllib.loads(Path("pyproject.toml").read_text(encoding="utf-8"))
    names = {
        item.split(">=")[0]
        for item in config["project"]["optional-dependencies"]["runtime"]
    }

    assert {"accelerate", "bitsandbytes", "peft", "transformers"} <= names
