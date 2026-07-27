from __future__ import annotations

import sys
from pathlib import Path

import pytest

SCRIPTS_DIR = Path(__file__).resolve().parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS_DIR))

from build_ru_v6_gguf import build_commands, conversion_lock, ensure_free_space  # noqa: E402


def test_build_commands_keep_base_and_adapter_separate(tmp_path):
    commands = build_commands(tmp_path, "python.exe")
    text = "\n".join(" ".join(command) for command in commands)

    assert "convert_hf_to_gguf.py" in text
    assert "convert_lora_to_gguf.py" in text
    assert "llama-quantize.exe" in text
    assert "--lora" not in text
    assert "ru-llama3.1-8b-instruct-v6-lora.gguf" in text


def test_ensure_free_space_rejects_a_small_volume(tmp_path):
    with pytest.raises(RuntimeError, match="24 GiB"):
        ensure_free_space(tmp_path, free_bytes=1)


def test_conversion_lock_refuses_an_existing_lock(tmp_path):
    lock = tmp_path / ".tmp" / "ru-v6-gguf-conversion.lock"
    lock.parent.mkdir()
    lock.write_text("other-process", encoding="utf-8")

    with pytest.raises(RuntimeError, match="already running"):
        with conversion_lock(tmp_path):
            raise AssertionError("existing lock must prevent conversion")

    assert lock.read_text(encoding="utf-8") == "other-process"
