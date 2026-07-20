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


def test_verify_local_qwen_base_accepts_qwen3_weights(tmp_path):
    (tmp_path / "config.json").write_text('{"model_type":"qwen3"}', encoding="utf-8")
    (tmp_path / "model.safetensors").write_bytes(b"weights")

    assert verify_local_qwen_base(tmp_path)["model_type"] == "qwen3"


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


def test_v5_profile_is_the_registered_local_qwen_3b_base():
    from mini_gpt.qwen_runtime import QWEN_V5_BASE, qwen_base_profile

    assert QWEN_V5_BASE.model_id == "Qwen/Qwen2.5-3B-Instruct"
    assert QWEN_V5_BASE.local_path == "models/qwen2.5-3b-instruct"
    assert QWEN_V5_BASE.label == "Ru V5 Qwen 3B"
    assert qwen_base_profile(QWEN_V5_BASE.model_id) == QWEN_V5_BASE


def test_qwen3_profile_is_registered_with_non_thinking_generation():
    from mini_gpt.qwen_runtime import QWEN3_4B_BASE, qwen_base_profile

    assert QWEN3_4B_BASE.model_id == "Qwen/Qwen3-4B"
    assert QWEN3_4B_BASE.local_path == "models/qwen3-4b"
    assert QWEN3_4B_BASE.label == "Ru Qwen3 4B"
    assert QWEN3_4B_BASE.disable_thinking is True
    assert qwen_base_profile(QWEN3_4B_BASE.model_id) == QWEN3_4B_BASE


def test_unknown_qwen_profile_is_rejected():
    from mini_gpt.qwen_runtime import qwen_base_profile

    with pytest.raises(ValueError, match="registered Ru Qwen base"):
        qwen_base_profile("other/model")


def test_v4_qwen_base_downloader_exposes_help_without_downloading():
    script = Path(__file__).resolve().parents[1] / "scripts" / "download_ru_qwen_v4_base.py"

    result = subprocess.run([sys.executable, str(script), "--help"], capture_output=True, text=True, check=False)

    assert result.returncode == 0
    assert "Download and verify Ru's V4 Qwen 1.5B base" in result.stdout


def test_v5_qwen_base_downloader_exposes_help_without_downloading():
    script = Path(__file__).resolve().parents[1] / "scripts" / "download_ru_qwen_v5_base.py"

    result = subprocess.run([sys.executable, str(script), "--help"], capture_output=True, text=True, check=False)

    assert result.returncode == 0
    assert "Download and verify Ru's V5 Qwen 3B base" in result.stdout


def test_qwen_generation_uses_deterministic_conversation_settings(monkeypatch):
    import mini_gpt.qwen_runtime as runtime

    class FakeInput:
        shape = (1, 3)

        def to(self, device):
            assert device == "cuda"
            return self

        def new_ones(self, shape):
            assert shape == self.shape
            return self

    class FakeTokenizer:
        def apply_chat_template(self, messages, **kwargs):
            assert messages[-1] == {"role": "user", "content": "Hello Ru"}
            assert kwargs == {"tokenize": True, "add_generation_prompt": True, "return_tensors": "pt"}
            return FakeInput()

        def decode(self, token_ids, **kwargs):
            assert token_ids == [4, 5]
            assert kwargs == {"skip_special_tokens": True}
            return "Hello back"

    class FakeModel:
        def generate(self, **kwargs):
            assert kwargs["input_ids"].shape == (1, 3)
            assert kwargs["attention_mask"].shape == (1, 3)
            assert kwargs["do_sample"] is False
            assert kwargs["max_new_tokens"] == 96
            assert kwargs["repetition_penalty"] == 1.05
            return [[1, 2, 3, 4, 5]]

    monkeypatch.setattr(runtime, "_load_qwen_for_generation", lambda profile, adapter_dir: (FakeTokenizer(), FakeModel()))
    response = runtime.generate_qwen_response(runtime.QWEN_V4_BASE, (("system", "You are Ru"), ("user", "Hello Ru")))

    assert response == "Hello back"


def test_qwen3_generation_disables_thinking_in_the_chat_template(monkeypatch):
    import mini_gpt.qwen_runtime as runtime

    class FakeInput:
        shape = (1, 2)

        def to(self, device):
            assert device == "cuda"
            return self

        def new_ones(self, shape):
            assert shape == self.shape
            return self

    class FakeTokenizer:
        def apply_chat_template(self, messages, **kwargs):
            assert messages[-1] == {"role": "user", "content": "Hello Ru"}
            assert kwargs == {
                "tokenize": True,
                "add_generation_prompt": True,
                "return_tensors": "pt",
                "enable_thinking": False,
            }
            return FakeInput()

        def decode(self, token_ids, **kwargs):
            assert token_ids == [3]
            assert kwargs == {"skip_special_tokens": True}
            return "Hi anh"

    class FakeModel:
        def generate(self, **kwargs):
            assert kwargs["attention_mask"].shape == (1, 2)
            return [[1, 2, 3]]

    monkeypatch.setattr(runtime, "_load_qwen_for_generation", lambda profile, adapter_dir: (FakeTokenizer(), FakeModel()))
    response = runtime.generate_qwen_response(runtime.QWEN3_4B_BASE, (("system", "You are Ru"), ("user", "Hello Ru")))

    assert response == "Hi anh"
