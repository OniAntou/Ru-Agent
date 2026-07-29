from __future__ import annotations

from pathlib import Path


RUNNER = Path(__file__).resolve().parents[1] / "scripts" / "run_omnivoice_pronunciation_tuning.ps1"


def _script() -> str:
    return RUNNER.read_text(encoding="utf-8")


def test_runner_defines_the_full_d_only_pronunciation_matrix() -> None:
    script = _script()

    assert '"D:\\LLM\\eval_results\\omnivoice-pronunciation"' in script
    assert '"data\\evaluations\\ru_tts_benchmark_v1.jsonl"' in script
    assert '"omnivoice_env\\Scripts\\python.exe"' in script
    assert '"gwen_env\\Scripts\\python.exe"' in script
    assert '"baseline-16", "vi-normalized-16", "vi-normalized-20", "vi-normalized-24"' in script
    assert '"ru", "yen_nhi"' in script
    assert '"tuning-summary.json"' in script
    assert "[switch]$ConfirmRun" in script
    assert "[switch]$WhatIf" in script
    assert "[switch]$OverwriteOutput" in script
    assert "--voice-profile $profile" in script
    assert "--omnivoice-config $configId" in script


def test_runner_has_an_explicit_no_side_effect_preflight_and_safe_process_scope() -> None:
    script = _script()

    assert "if ($WhatIf)" in script
    assert "if (-not $ConfirmRun)" in script
    assert "no directory, service, CUDA load, settings change, or WAV" in script
    assert "Get-RuComputeBlocker" in script
    assert "llama-server|llama-cli|mini_gpt\\.transformers|transformers.*mini_gpt" in script
    assert "Test-OmniVoiceServiceLoaded" in script
    assert "http://127.0.0.1:5052/status" in script
    assert "OmniVoice service is loaded" in script
    assert "Stop-Process" not in script
    assert "pip install" not in script
    assert "setup_omnivoice_env.ps1" not in script
    assert "mini_gpt.gui" not in script
    assert "Start-Process -FilePath $gwenPython" in script
    assert "gwen_tts_server.py" in script
    assert "Release-Gwen" in script
    assert "finally" in script


def test_runner_records_the_manual_review_gate_without_adopting_runtime_settings() -> None:
    script = _script()

    assert 'status = "pending_manual_review"' in script
    assert 'automatic_speed_verdict = $verdict' in script
    assert "median_warm_first_playable_wav_seconds" in script
    assert "failed_records" in script
    assert "manual_decision" not in script
    assert "omnivoice_tts_server.py" not in script
