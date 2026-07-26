from __future__ import annotations

import subprocess
import time

import pytest

import mini_gpt.llama3_1_training as training
from mini_gpt.llama3_1_training import (
    PhysicalVramMonitor,
    read_nvidia_smi_memory_mib,
    write_adapter_manifest,
)


def test_read_nvidia_smi_memory_mib_parses_first_gpu(monkeypatch):
    def fake_run(*args, **kwargs):
        del args, kwargs
        return subprocess.CompletedProcess(
            args=["nvidia-smi"],
            returncode=0,
            stdout="5845\n",
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
