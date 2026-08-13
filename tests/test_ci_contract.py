from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_ci_uses_current_python_extras_and_route_contract() -> None:
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    assert ".[dev,runtime,gguf]" in workflow
    assert "dev,server" not in workflow
    assert "scripts/check_aspnetcore_route_matrix.py" in workflow
    assert "python -m pytest -q" in workflow
    assert "pip check" in workflow


def test_ci_runs_both_dotnet_test_projects() -> None:
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    assert "backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj" in workflow
    assert "gui-winui3.Tests/gui-winui3.Tests.csproj" in workflow
    assert "dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64" in workflow
