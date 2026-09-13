from __future__ import annotations

import tomllib
from pathlib import Path
from typing import Any, cast

PROJECT_ROOT = Path(__file__).resolve().parents[1]


def _project_metadata() -> dict[str, Any]:
    with (PROJECT_ROOT / "pyproject.toml").open("rb") as project_file:
        return cast(dict[str, Any], tomllib.load(project_file)["project"])


def test_python_distribution_is_tooling_only() -> None:
    metadata = _project_metadata()
    extras = metadata["optional-dependencies"]

    assert metadata["dependencies"] == []
    assert "voice" not in extras
    assert "runtime" not in extras
    assert any(requirement.startswith("pytest") for requirement in extras["dev"])
    assert any(requirement.startswith("ruff") for requirement in extras["dev"])
    assert any(requirement.startswith("httpx") for requirement in extras["tooling"])
