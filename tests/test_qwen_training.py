import json

import pytest

from mini_gpt.qwen_runtime import QWEN_V4_BASE
from mini_gpt.qwen_training import QwenTrainingConfig, write_adapter_manifest


def test_training_config_accepts_registered_v4_base_and_rejects_unknown_model():
    config = QwenTrainingConfig(base_model_id=QWEN_V4_BASE.model_id)

    assert config.base_model_path == QWEN_V4_BASE.local_path
    with pytest.raises(ValueError, match="registered Ru Qwen base"):
        QwenTrainingConfig(base_model_id="other/model")


def test_adapter_manifest_records_reproducible_identity(tmp_path):
    manifest = write_adapter_manifest(
        tmp_path,
        backend="qwen-qlora-v4",
        profile_name="ru_qwen_v4_1_5b",
        base_model_id=QWEN_V4_BASE.model_id,
        base_model_path="D:/LLM/models/qwen2.5-1.5b-instruct",
        dataset_sha256="abc",
        best_validation_loss=1.25,
        peak_vram_mb=1200.0,
        seed=42,
        max_length=512,
    )

    assert manifest["backend"] == "qwen-qlora-v4"
    assert manifest["profile_name"] == "ru_qwen_v4_1_5b"
    assert json.loads((tmp_path / "ru_manifest.json").read_text(encoding="utf-8")) == manifest
