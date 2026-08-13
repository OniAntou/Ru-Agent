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

    launch_plan = (
        root / "gui-winui3" / "Services" / "BackendLaunchPlan.cs"
    ).read_text(encoding="utf-8")
    assert "RU_BACKEND_HOST_MODE" not in launch_plan
    assert "ru_runtime.server" not in launch_plan

    program = (root / "backend-dotnet" / "RuBackend" / "Program.cs").read_text(
        encoding="utf-8"
    )
    assert "HttpForwarder" not in program
    assert "WebSocketRelay" not in program
    assert "UpstreamUrl" not in program
    assert not list(
        (root / "backend-dotnet" / "RuBackend" / "Compatibility").glob("*.cs")
    )
