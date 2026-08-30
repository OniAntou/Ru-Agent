from __future__ import annotations

from pathlib import Path


def test_removed_python_web_host_and_forwarders_cannot_return_silently() -> None:
    root = Path(__file__).resolve().parents[1]

    assert not (root / "src" / "mini_gpt").exists()
    assert not (root / "src" / "ru_runtime" / "server.py").exists()
    assert not (root / "src" / "ru_runtime" / "routers").exists()

    project = (root / "pyproject.toml").read_text(encoding="utf-8").casefold()
    assert '"fastapi' not in project
    assert '"uvicorn' not in project
    assert '"pydantic' not in project

    # Legacy WinUI ownership file was intentionally removed in phase-1 (Public Host now owns Worker/Broker)
    assert not (root / "gui-winui3" / "Services" / "BackendLaunchPlan.cs").exists()
    assert not (root / "gui-winui3" / "Services" / "BackendProcessManager.cs").exists()
    hosting = (root / "backend-dotnet" / "RuAgent.Hosting" / "WorkerBrokerLaunchPlan.cs").read_text(encoding="utf-8")
    assert "RU_BACKEND_HOST_MODE" not in hosting
    # The managed launch plan must not retain a Python multiplexer reference.
    assert "from ru_runtime.server import" not in hosting
    assert "ru_runtime.worker.server" not in hosting
    assert '"dotnet-agent-worker"' in hosting

    program = (root / "backend-dotnet" / "RuBackend" / "Program.cs").read_text(
        encoding="utf-8"
    )
    assert "HttpForwarder" not in program
    assert "WebSocketRelay" not in program
    assert "UpstreamUrl" not in program
    assert not list(
        (root / "backend-dotnet" / "RuBackend" / "Compatibility").glob("*.cs")
    )
