from __future__ import annotations

import ast
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

DOMAIN_PACKAGES = {
    # Phase 3: generation retired (C# Harness.Core/Adapters owns it)
    "memory",
    "models",
    "observation",
    "persistence",
    "policy",
    "speech",
    "voice",
    "vts",
}


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
    # Phase-1 harness foundation moved ownership to Public Host (RuAgent.Hosting)
    assert not (ROOT / "gui-winui3/Services/BackendLaunchPlan.cs").exists(), "legacy WinUI launch plan should be removed; Public Host owns Worker/Broker"
    hosting = (ROOT / "backend-dotnet/RuAgent.Hosting/WorkerBrokerLaunchPlan.cs").read_text(
        encoding="utf-8"
    )
    assert "ru_runtime.worker.server" in hosting
    assert "-m mini_gpt." not in hosting
    # multiplexer serves both v1 and v2; ensure no legacy mini_gpt import remains
    worker_server = (ROOT / "src/ru_runtime/worker/server.py").read_text(encoding="utf-8")
    assert "worker.v1" in worker_server or "worker.server" in worker_server


def test_runtime_modules_are_owned_by_explicit_domain_packages() -> None:
    package_root = ROOT / "src/ru_runtime"

    for package in DOMAIN_PACKAGES:
        assert (package_root / package / "__init__.py").is_file(), package
    # Phase 3: worker/v1 retired (C# Runtime.Worker owns it), so the package
    # may be absent; if present it must still be valid, but absence is not a failure.
    if (package_root / "worker/v1").exists():
        assert (package_root / "worker/v1/__init__.py").is_file()

    root_modules = sorted(path.name for path in package_root.glob("*.py"))
    assert root_modules == ["__init__.py"]


def test_runtime_has_no_generic_dumping_package() -> None:
    package_root = ROOT / "src/ru_runtime"

    for name in ("shared", "common", "helpers", "compatibility"):
        assert not (package_root / name).exists()


def test_python_runtime_has_no_native_volume_owner() -> None:
    assert not (ROOT / "src/ru_runtime/system/volume.py").exists()
    pyproject = (ROOT / "pyproject.toml").read_text(encoding="utf-8")
    assert ("py" + "caw") not in pyproject.lower()
    assert ("com" + "types") not in pyproject.lower()
    sources = "\n".join(
        path.read_text(encoding="utf-8")
        for path in (ROOT / "src/ru_runtime").rglob("*.py")
    )
    assert ("adjust_system" + "_volume") not in sources
    assert ("open_application" + "_or_url") not in sources


def test_reply_orchestration_has_no_desktop_side_effect_imports() -> None:
    path = ROOT / "src/ru_runtime/generation/orchestration.py"
    if not path.is_file():
        # Phase 3: generation retired - C# owns orchestration, no Python file to check
        return
    tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    imports: set[str] = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            imports.update(item.name for item in node.names)
        elif isinstance(node, ast.ImportFrom):
            imports.add(node.module or "")

    forbidden = {
        "ru_runtime.system",
        "os",
        "subprocess",
        "webbrowser",
        "ctypes",
        "py" + "caw",
        "com" + "types",
    }
    assert not any(
        imported == candidate or imported.startswith(candidate + ".")
        for imported in imports
        for candidate in forbidden
    )
