import json

import pytest

from mini_gpt.qwen_training import QwenTrainingConfig, write_adapter_manifest


def test_qwen_training_config_rejects_a_model_other_than_the_approved_base():
    with pytest.raises(ValueError, match="Qwen/Qwen2.5-0.5B-Instruct"):
        QwenTrainingConfig(base_model_id="other/model")


def test_adapter_manifest_records_reproducible_identity(tmp_path):
    manifest = write_adapter_manifest(
        tmp_path,
        base_model_id="Qwen/Qwen2.5-0.5B-Instruct",
        base_model_path="D:/LLM/models/qwen2.5-0.5b-instruct",
        dataset_sha256="abc",
        best_validation_loss=1.25,
        peak_vram_mb=1200.0,
        seed=42,
        max_length=512,
    )

    assert manifest["backend"] == "qwen-qlora-v3"
    assert json.loads((tmp_path / "ru_manifest.json").read_text(encoding="utf-8")) == manifest
