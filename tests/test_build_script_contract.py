from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_release_build_publishes_managed_worker_before_backend() -> None:
    script = (ROOT / "build.bat").read_text(encoding="utf-8")

    worker_project = r"backend-dotnet\RuAgent.Runtime.Worker\RuAgent.Runtime.Worker.csproj"
    backend_project = r"backend-dotnet\RuBackend\RuBackend.csproj"

    assert worker_project in script
    assert backend_project in script
    assert script.index(worker_project) < script.index(backend_project)
    assert 'set "WORKER_PUBLISH_DIR=%~dp0backend-dotnet\\RuAgent.Runtime.Worker\\bin\\Release\\net10.0\\win-x64\\publish"' in script
    assert r"%WORKER_PUBLISH_DIR%\RuAgent.Runtime.Worker.exe" in script


def test_build_fails_when_the_managed_worker_artifact_is_missing() -> None:
    script = (ROOT / "build.bat").read_text(encoding="utf-8")

    assert "RuAgent.Runtime.Worker.exe was not created" in script
    assert "if not exist" in script


def test_release_gui_publish_forces_x64_self_contained_output() -> None:
    script = (ROOT / "build.bat").read_text(encoding="utf-8")

    assert 'if exist "%PUBLISH_DIR%" (' in script
    assert 'rmdir /s /q "%PUBLISH_DIR%"' in script
    assert "-p:Platform=x64" in script
    assert "-p:RuntimeIdentifier=win-x64" in script
    assert "-p:SelfContained=true" in script
    assert "RuApp.runtimeconfig.json" in script
    assert 'findstr /C:"includedFrameworks"' in script


def test_start_dev_uses_a_startup_probe_for_immediate_gui_failure() -> None:
    script = (ROOT / "start_dev.bat").read_text(encoding="utf-8")

    assert r"scripts\launch_gui.ps1" in script
    assert "-ExecutablePath" in script
    assert '-WorkingDirectory "%~dp0."' in script

    launcher = (ROOT / "scripts" / "launch_gui.ps1").read_text(encoding="utf-8")
    assert "$process.ExitCode -ne 0" in launcher
