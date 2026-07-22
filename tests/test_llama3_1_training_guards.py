from __future__ import annotations

import subprocess
import time

import pytest

import mini_gpt.llama3_1_training as training
from mini_gpt.llama3_1_training import PhysicalVramMonitor, read_nvidia_smi_memory_mib


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
