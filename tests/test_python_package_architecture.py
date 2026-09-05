"""Package/import ownership, not a history of deleted files."""

from __future__ import annotations

import ast
import re
import tomllib
from importlib.util import resolve_name
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RETIRED_MODULES = (
    "mini_gpt", "ru_runtime.generation", "ru_runtime.worker", "ru_runtime.vts",
    "ru_runtime.server", "ru_runtime.routers",
)


def test_active_python_imports_do_not_cross_retired_runtime_boundaries() -> None:
    offenders = []
    for folder in ("src", "scripts", "tools", "tests"):
        for path in (ROOT / folder).rglob("*.py"):
            if {"__pycache__", "bin", "obj", "out"}.intersection(path.parts):
                continue
            tree = ast.parse(path.read_text(encoding="utf-8-sig"), filename=str(path))
            import_root = ROOT / "src" if folder == "src" else ROOT
            module = ".".join(path.relative_to(import_root).with_suffix("").parts)
            if folder == "src" and any(
                module == retired or module.startswith(retired + ".")
                for retired in RETIRED_MODULES
            ):
                offenders.append(f"retired source package: {path.relative_to(ROOT)}")
            for node in ast.walk(tree):
                names = []
                if isinstance(node, ast.Import):
                    names = [item.name for item in node.names]
                elif isinstance(node, ast.ImportFrom):
                    base = node.module or ""
                    if node.level:
                        base = resolve_name("." * node.level + base, module.rpartition(".")[0])
                    names = [base, *(f"{base}.{item.name}" for item in node.names)]
                if any(
                    name == retired or name.startswith(retired + ".")
                    for name in names for retired in RETIRED_MODULES
                ):
                    offenders.append(f"{path.relative_to(ROOT)}:{getattr(node, 'lineno', 0)}")
    assert offenders == []


def test_python_distribution_excludes_retired_host_and_device_dependencies() -> None:
    project = tomllib.loads((ROOT / "pyproject.toml").read_text(encoding="utf-8"))["project"]
    dependencies = list(project["dependencies"])
    for group in project["optional-dependencies"].values():
        dependencies.extend(group)
    names = {re.split(r"[\s\[<>=!~;]", requirement, maxsplit=1)[0].lower() for requirement in dependencies}
    assert names.isdisjoint({"fastapi", "uvicorn", "pyvts", "pygame", "pycaw", "comtypes"})
