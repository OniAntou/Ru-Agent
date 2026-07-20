import pytest
import subprocess
import sys
from pathlib import Path

from mini_gpt.qwen_runtime import qwen_dependency_status, require_qwen_dependencies


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
