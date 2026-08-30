from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def _source_files(*roots: Path) -> list[Path]:
    return [
        path
        for root in roots
        for path in root.rglob("*")
        if path.is_file() and path.suffix in {".cs", ".csproj", ".py", ".bat", ".md"}
        and not {"bin", "obj", ".git", "__pycache__"}.intersection(path.parts)
    ]


def test_retired_python_worker_and_generation_packages_are_absent() -> None:
    for relative in (
        "src/ru_runtime/generation/__init__.py",
        "src/ru_runtime/worker/__init__.py",
        "src/ru_runtime/worker/server.py",
    ):
        assert not (ROOT / relative).exists()


def test_current_startup_and_harness_sources_do_not_reference_retired_worker_modules() -> None:
    paths = _source_files(
        ROOT / "backend-dotnet" / "RuAgent.Hosting",
        ROOT / "backend-dotnet" / "RuBackend",
        ROOT / "scripts" / "harness",
        ROOT / "src" / "ru_runtime",
    )
    forbidden = (
        "ru_runtime.generation",
        "ru_runtime.worker.v1",
        "ru_runtime.worker.v2",
        "ru_runtime.worker.server",
        "RuntimeMigrationMode.PythonCompatibility",
    )
    for path in paths:
        source = path.read_text(encoding="utf-8")
        assert not any(value in source for value in forbidden), path


def test_current_launch_surfaces_do_not_publish_legacy_runtime_configuration() -> None:
    paths = _source_files(
        ROOT / "backend-dotnet" / "RuAgent.Hosting",
        ROOT / "backend-dotnet" / "RuBackend",
        ROOT / "scripts" / "harness",
    )
    forbidden = ("python-agent-compat-worker", "python-device-worker", "RU_WORKER_TOKEN")
    for path in paths:
        source = path.read_text(encoding="utf-8")
        assert not any(value in source for value in forbidden), path
