from __future__ import annotations

import ast
import tomllib
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_distribution_and_import_package_use_the_ru_identity() -> None:
    project = tomllib.loads((ROOT / "pyproject.toml").read_text(encoding="utf-8"))

    assert project["project"]["name"] == "ru-runtime"
    assert (ROOT / "src/ru_runtime/__init__.py").is_file()
    assert not (ROOT / "src/mini_gpt").exists()


def test_active_python_has_no_legacy_package_imports() -> None:
    offenders: list[str] = []
    for folder in (ROOT / "src", ROOT / "scripts", ROOT / "tests"):
        for path in folder.rglob("*.py"):
            tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
            for node in ast.walk(tree):
                if isinstance(node, ast.Import):
                    names = [item.name for item in node.names]
                elif isinstance(node, ast.ImportFrom):
                    names = [node.module or ""]
                else:
                    continue
                if any(name == "mini_gpt" or name.startswith("mini_gpt.") for name in names):
                    offenders.append(path.relative_to(ROOT).as_posix())
                    break
    assert offenders == []


def test_desktop_launches_the_new_worker_module() -> None:
    source = (ROOT / "gui-winui3/Services/BackendLaunchPlan.cs").read_text(
        encoding="utf-8"
    )
    assert "-m ru_runtime.worker_server" in source
    assert "-m mini_gpt." not in source
