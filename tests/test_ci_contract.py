from __future__ import annotations

import re
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_ci_python_job_uses_supported_runtime_and_quality_gates() -> None:
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    assert ".[dev,tooling]" in workflow
    assert "dev,server" not in workflow
    assert "python -m pytest -q" in workflow
    assert 'python-version: ["3.11", "3.12", "3.13"]' in workflow
    assert "python-version: ${{ matrix.python-version }}" in workflow
    assert "python scripts/check_aspnetcore_route_matrix.py" in workflow
    # The interpreter boundary is enforced, not just inventoried: the product is .NET
    # only, so the check fails when an interpreter file appears under a product root.
    assert "python scripts/check_python_runtime_boundary.py" in workflow
    assert "retired-compat-harness" not in workflow
    assert "ru-harness-vnext.json" not in workflow
    assert "python -m ruff check tests scripts tools" in workflow
    assert "python -m mypy" not in workflow
    assert "pip check" in workflow


def test_ci_never_hides_findings_behind_permanent_escapes() -> None:
    """verification.md: ruff and pip check findings are never suppressed.

    A gate that can be marked non-blocking (or short-circuited to success) stops
    being a gate, so the workflow must not carry those escapes at all.
    """
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    assert "continue-on-error" not in workflow
    assert "|| true" not in workflow
    assert "if: false" not in workflow


def test_ci_uses_reusable_dotnet_runner_and_keeps_build_steps() -> None:
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    dotnet_job = workflow.split("  dotnet:\n", maxsplit=1)[1]

    assert "shell: pwsh" in dotnet_job
    assert r"run: .\scripts\test_dotnet.ps1 -Configuration Debug" in dotnet_job
    assert "dotnet test backend-dotnet/" not in dotnet_job
    assert "dotnet test gui-winui3.Tests/" not in dotnet_job
    assert "dotnet build gui-winui3.Harness/RuApp.Harness.csproj -c Release" in workflow
    assert "dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64 -p:Platform=x64 -p:SelfContained=true" in workflow
    assert "cancel-in-progress: true" in workflow
    # git diff --check on a fresh checkout has nothing to compare and can never
    # fail; git log --check actually inspects the pushed patches for whitespace
    # errors, which is what this gate exists for.
    assert "git log --check" in workflow


def test_ci_publish_smoke_and_dependency_audits_are_wired_but_advisory() -> None:
    """The publish path is smoked and vulnerability scans stay advisory.

    The publish smoke must exercise the self-contained Release layout instead
    of another Debug build, and the rpc smoke must keep its dynamic-port
    default rather than pinning a port that a busy machine can already hold.
    Each audit escapes only its own exit code; continue-on-error stays banned,
    so no build or test gate can be marked non-blocking by this addition.
    """
    workflow = (ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
    dotnet_job = workflow.split("  dotnet:\n", maxsplit=1)[1].split("  node:\n", maxsplit=1)[0]
    node_job = workflow.split("  node:\n", maxsplit=1)[1]

    assert r"run: .\scripts\rpc_smoke.ps1" in dotnet_job
    assert r"run: .\scripts\rpc_smoke.ps1 -Port" not in workflow
    assert r"run: .\scripts\publish_smoke.ps1" in dotnet_job
    assert (ROOT / "scripts" / "publish_smoke.ps1").is_file()

    assert "dotnet list RuAgent.slnx package --vulnerable --include-transitive || exit 0" in dotnet_job
    assert "npm audit --prefix sdk/ru-agent --omit=dev || exit 0" in node_job
    # pip-audit is deliberately not wired: pyproject pins no such tool, and the
    # repo does not grow a dependency just to run an advisory scan.
    assert "pip-audit" not in workflow
    assert "continue-on-error" not in workflow


def test_ruff_targets_the_minimum_supported_python() -> None:
    config = tomllib.loads((ROOT / "pyproject.toml").read_text(encoding="utf-8"))
    requires_python = config["project"]["requires-python"]
    minimum = re.search(r">=(\d+)\.(\d+)", requires_python)

    assert minimum is not None
    major, minor = minimum.groups()
    assert config["tool"]["ruff"]["target-version"] == f"py{major}{minor}"
