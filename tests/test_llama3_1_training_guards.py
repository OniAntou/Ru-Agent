from __future__ import annotations

import subprocess
import time

import pytest

import mini_gpt.llama3_1_training as training
from mini_gpt.llama3_1_training import (
    LlamaTrainingConfig,
    PhysicalVramMonitor,
    read_nvidia_smi_memory_mib,
    resolve_training_step_limit,
    select_validation_rows,
    write_adapter_manifest,
)


def test_read_nvidia_smi_memory_mib_parses_first_gpu(monkeypatch):
    def fake_run(*args, **kwargs):
        del args, kwargs
        return subprocess.CompletedProcess(
            args=["nvidia-smi"],
            returncode=0,
            stdout="5845, 6144\n",
            stderr="",
        )

    monkeypatch.setattr(training.subprocess, "run", fake_run)

    assert read_nvidia_smi_memory_mib() == 5845.0


@pytest.mark.parametrize(
    "error",
    [FileNotFoundError(), subprocess.TimeoutExpired("nvidia-smi", 2)],
)
def test_read_nvidia_smi_memory_mib_returns_none_on_tool_failure(monkeypatch, error):
    def fake_run(*args, **kwargs):
        del args, kwargs
        raise error

    monkeypatch.setattr(training.subprocess, "run", fake_run)

    assert read_nvidia_smi_memory_mib() is None


def test_physical_vram_monitor_records_peak_and_cap_breach(monkeypatch):
    monkeypatch.setattr(training, "read_nvidia_smi_memory_mib", lambda: 6200.0)
    monitor = PhysicalVramMonitor(cap_mib=6144, poll_seconds=0.001)

    monitor.start()
    time.sleep(0.01)
    monitor.stop()

    assert monitor.peak_mib == 6200.0
    assert monitor.exceeded is True


def test_adapter_manifest_records_continuation_source(tmp_path):
    manifest = write_adapter_manifest(
        tmp_path / "adapter",
        backend="llama3.1-qlora-continuation",
        profile_name="ru_llama3_1_v6_1_from_v6",
        base_model_id="test-base",
        base_model_path="D:/LLM/models/llama3.1-8b-instruct-hf",
        source_adapter_dir="D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6",
        dataset_sha256="dataset-hash",
        best_validation_loss=1.25,
        peak_vram_mb=5120.0,
        seed=42,
        max_length=512,
    )

    assert manifest["adapter_dir"].endswith("adapter")
    assert manifest["source_adapter_dir"].endswith("ru-llama3.1-8b-instruct-v6")


def test_training_step_limit_prefers_cli_then_configured_budget():
    config = LlamaTrainingConfig(
        base_model_id="test-base",
        base_model_path="D:/LLM/models/test-base",
        epochs=3,
        max_steps=640,
    )

    assert resolve_training_step_limit(config, train_row_count=1680) == 640
    assert resolve_training_step_limit(config, train_row_count=1680, cli_max_steps=32) == 32


def test_v6_2_fresh_config_is_loadable_and_bounded():
    config = LlamaTrainingConfig.from_json("configs/ru_llama3_1_v6_2_fresh.json")

    assert config.max_steps == 480
    assert config.wall_clock_seconds == 9600
    assert config.physical_vram_cap_mib == 6144


def test_training_step_limit_falls_back_to_full_epochs_and_rejects_nonpositive_budget():
    config = LlamaTrainingConfig(
        base_model_id="test-base",
        base_model_path="D:/LLM/models/test-base",
        epochs=3,
    )

    assert resolve_training_step_limit(config, train_row_count=1680) == 5040
    with pytest.raises(ValueError, match="max_steps must be positive"):
        resolve_training_step_limit(config, train_row_count=1680, cli_max_steps=0)


def test_validation_sample_is_bounded_deterministic_and_keeps_source_order():
    rows = list(range(360))

    selected = select_validation_rows(rows, max_examples=32, seed=42)

    assert len(selected) == 32
    assert selected == select_validation_rows(rows, max_examples=32, seed=42)
    assert selected == sorted(selected)
    assert select_validation_rows(rows, max_examples=400, seed=42) == rows
    with pytest.raises(ValueError, match="validation_max_examples must be positive"):
        select_validation_rows(rows, max_examples=0, seed=42)
