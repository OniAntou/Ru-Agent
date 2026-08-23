from __future__ import annotations

import re
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_ci_uses_current_python_extras_and_route_contract() -> None:
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    assert ".[dev,runtime]" in workflow
    assert "dev,server" not in workflow
    assert "scripts/check_aspnetcore_route_matrix.py" in workflow
    assert "--mode backend-deterministic" in workflow
    assert "actions/upload-artifact@v4" in workflow
    assert "ru-harness-vnext-py${{ matrix.python-version }}" in workflow
    assert "python -m pytest -q" in workflow
    assert 'python-version: ["3.11", "3.13"]' in workflow
    assert "python-version: ${{ matrix.python-version }}" in workflow
    assert "python -m ruff check src tests scripts tools" in workflow
    assert "python -m mypy src" in workflow
    assert "pip check" in workflow


def test_ci_runs_both_dotnet_test_projects() -> None:
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    assert "backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj" in workflow
    assert "backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj" in workflow
    assert "backend-dotnet/RuAgent.Connectors.Tests/RuAgent.Connectors.Tests.csproj" in workflow
    assert "gui-winui3.Tests/gui-winui3.Tests.csproj" in workflow
    assert "dotnet build gui-winui3.Harness/RuApp.Harness.csproj -c Release" in workflow
    assert "dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64" in workflow


def test_static_analysis_targets_the_minimum_supported_python() -> None:
    config = tomllib.loads((ROOT / "pyproject.toml").read_text(encoding="utf-8"))
    requires_python = config["project"]["requires-python"]
    minimum = re.search(r">=(\d+)\.(\d+)", requires_python)

    assert minimum is not None
    major, minor = minimum.groups()
    assert config["tool"]["ruff"]["target-version"] == f"py{major}{minor}"
    assert config["tool"]["mypy"]["python_version"] == f"{major}.{minor}"
    assert config["tool"]["mypy"]["check_untyped_defs"] is True


def test_gui_harness_is_manual_and_uses_the_interactive_runner() -> None:
    workflow = (ROOT / ".github" / "workflows" / "gui-harness.yml").read_text(encoding="utf-8")

    assert "workflow_dispatch:" in workflow
    assert "self-hosted" in workflow
    assert "windows" in workflow
    assert "ru-gui" in workflow
    assert "scripts\\run_gui_voice_harness.ps1" in workflow
    assert "ru-harness-vnext-gui" in workflow
