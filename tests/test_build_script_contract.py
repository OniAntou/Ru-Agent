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
