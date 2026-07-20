import pytest
import subprocess
import sys
from pathlib import Path

from mini_gpt.qwen_runtime import qwen_dependency_status, require_qwen_dependencies, verify_local_qwen_base


def test_qwen_dependency_status_reports_missing_packages_without_importing_them():
    installed = {"transformers": True, "accelerate": False, "peft": True, "bitsandbytes": False}

    resolver = lambda name: object() if installed[name] else None
    assert qwen_dependency_status(resolver) == installed

    with pytest.raises(RuntimeError, match="accelerate, bitsandbytes"):
        require_qwen_dependencies(resolver)


def test_qwen_preflight_script_exposes_help_without_optional_packages():
    script = Path(__file__).resolve().parents[1] / "scripts" / "qwen_preflight.py"

    result = subprocess.run([sys.executable, str(script), "--help"], capture_output=True, text=True, check=False)

    assert result.returncode == 0
    assert "Check the local Qwen CUDA runtime" in result.stdout


def test_verify_local_qwen_base_rejects_the_wrong_model_type(tmp_path):
    (tmp_path / "config.json").write_text('{"model_type":"gpt2"}', encoding="utf-8")

    with pytest.raises(ValueError, match="qwen2"):
        verify_local_qwen_base(tmp_path)


def test_qwen_base_downloader_exposes_help_without_downloading():
    script = Path(__file__).resolve().parents[1] / "scripts" / "download_ru_qwen_base.py"

    result = subprocess.run([sys.executable, str(script), "--help"], capture_output=True, text=True, check=False)

    assert result.returncode == 0
    assert "Download and verify Ru's local Qwen base" in result.stdout


def test_v4_profile_is_the_registered_local_qwen_1_5b_base():
    from mini_gpt.qwen_runtime import QWEN_V4_BASE, qwen_base_profile

    assert QWEN_V4_BASE.model_id == "Qwen/Qwen2.5-1.5B-Instruct"
    assert QWEN_V4_BASE.local_path == "models/qwen2.5-1.5b-instruct"
    assert qwen_base_profile(QWEN_V4_BASE.model_id) == QWEN_V4_BASE


def test_unknown_qwen_profile_is_rejected():
    from mini_gpt.qwen_runtime import qwen_base_profile

    with pytest.raises(ValueError, match="registered Ru Qwen base"):
        qwen_base_profile("other/model")


def test_v4_qwen_base_downloader_exposes_help_without_downloading():
    script = Path(__file__).resolve().parents[1] / "scripts" / "download_ru_qwen_v4_base.py"

    result = subprocess.run([sys.executable, str(script), "--help"], capture_output=True, text=True, check=False)

    assert result.returncode == 0
    assert "Download and verify Ru's V4 Qwen 1.5B base" in result.stdout
