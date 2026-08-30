"""Static release-build contract for the managed Phase 2 runtime."""

from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_release_build_publishes_runtime_core_with_the_single_managed_worker() -> None:
    script = (ROOT / "build.bat").read_text(encoding="utf-8")
    worker_project = r"backend-dotnet\RuAgent.Runtime.Worker\RuAgent.Runtime.Worker.csproj"
    backend_project = r"backend-dotnet\RuBackend\RuBackend.csproj"

    assert worker_project in script
    assert backend_project in script
    assert script.index(worker_project) < script.index(backend_project)
    assert "RUNTIME_CORE_PUBLISH_DLL" in script
    assert r"RuAgent.Runtime.Core.dll" in script
    assert "v1/v2" in script

    worker_project_text = (ROOT / "backend-dotnet/RuAgent.Runtime.Worker/RuAgent.Runtime.Worker.csproj").read_text(
        encoding="utf-8"
    )
    assert "RuAgent.Runtime.Core\\RuAgent.Runtime.Core.csproj" in worker_project_text


def test_startup_contract_requires_the_managed_worker_artifact() -> None:
    script = (ROOT / "start_dev.bat").read_text(encoding="utf-8")

    assert "RuAgent.Runtime.Worker\\bin\\Release" in script
    assert "Managed Worker publish output not found" in script
    assert "Debug" not in script


def test_default_topology_and_rollback_are_documented_together() -> None:
    launch_plan = (ROOT / "backend-dotnet/RuAgent.Hosting/WorkerBrokerLaunchPlan.cs").read_text(
        encoding="utf-8"
    )

    assert "--device-port" in launch_plan
    assert 'RuntimeMigrationMode.PythonCompatibility' not in launch_plan
    assert '"python-device-worker"' not in launch_plan
    assert 'ru_runtime.worker.server' not in launch_plan


def test_managed_startup_fails_closed_when_legacy_python_mode_is_selected() -> None:
    program = (ROOT / "backend-dotnet/RuBackend/Program.cs").read_text(encoding="utf-8")

    assert "ParseRuntimeMigrationMode" in program
    assert "RuntimeMigrationModeSelectionException" in program
    assert "RuntimeMigrationModeSelectionException.ErrorCode" in program
