"""Run the real PowerShell entrypoint against isolated projects and fake dotnet."""
from __future__ import annotations

import ctypes
import os
import shutil
import subprocess
from pathlib import Path
from uuid import uuid4

import pytest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts/test_dotnet.ps1"


@pytest.fixture
def workspace(tmp_path: Path) -> Path:
    for folder in ("backend-dotnet", "gui-winui3.Tests"):
        (tmp_path / folder).mkdir()
    return tmp_path


def _project(root: Path, relative: str) -> Path:
    project = root / relative
    project.parent.mkdir(parents=True, exist_ok=True)
    project.write_text("<Project />\n", encoding="utf-8")
    return project


def _run(root: Path, *options: str, missing_dotnet: bool = False,
         discovery_error: bool = False,
         mutex_name: str | None = None) -> subprocess.CompletedProcess[str]:
    pwsh = shutil.which("pwsh") or shutil.which("powershell")
    assert pwsh, "powershell or pwsh is required to test the runner"
    fake_bin = root / "fake-bin"
    fake_bin.mkdir()
    if not missing_dotnet:
        (fake_bin / "dotnet.cmd").write_text(
            '@echo off\n'
            f'>>"{root / "dotnet.log"}" echo %~1^|%~2^|%~3^|%~4^|%~5^|%~6\n'
            'if /I "%~nx2"=="02.Fail.Tests.csproj" exit /b 17\n'
            'exit /b 0\n', encoding="utf-8",
        )
    environment = os.environ.copy()
    environment["PATH"] = str(fake_bin) if missing_dotnet else os.pathsep.join((str(fake_bin), environment["PATH"]))
    if mutex_name is None:
        mutex_name = f"ru-test-runner-{uuid4().hex}"
    arguments = ["-File", str(SCRIPT), "-WorkspaceRoot", str(root), "-MutexName", mutex_name, *options]
    if discovery_error:
        quoted_script = str(SCRIPT).replace("'", "''")
        quoted_root = str(root).replace("'", "''")
        arguments = ["-Command", "function Get-ChildItem { throw 'injected discovery failure' }; "
                     f"& '{quoted_script}' -WorkspaceRoot '{quoted_root}' -MutexName '{mutex_name}'"]
    return subprocess.run(
        [pwsh, "-NoProfile", *arguments], cwd=root, env=environment,
        capture_output=True, text=True, check=False, timeout=30,
    )


def test_runner_discovers_nested_backend_and_gui_projects_in_stable_order(workspace: Path) -> None:
    projects = [_project(workspace, relative) for relative in (
        "backend-dotnet/Zeta.Tests/Zeta.Tests.csproj",
        "backend-dotnet/nested/Alpha.Tests/Alpha.Tests.csproj",
        "gui-winui3.Tests/gui-winui3.Tests.csproj",
        "gui-winui3.Tests/nested/Extra.Tests.csproj",
    )]
    _project(workspace, "backend-dotnet/Product/Product.csproj")
    _project(workspace, "backend-dotnet/Product/obj/Cached.Tests.csproj")
    result = _run(workspace)
    assert result.returncode == 0, result.stdout + result.stderr
    assert (workspace / "dotnet.log").read_text(encoding="utf-8").splitlines() == [
        f"test|{project}|--nologo|--configuration|Debug|"
        for project in sorted(projects, key=lambda path: str(path).lower())
    ]


def test_runner_propagates_first_failure_and_stops_following_projects(workspace: Path) -> None:
    projects = [_project(workspace, f"backend-dotnet/{name}/{name}.csproj")
                for name in ("01.Pass.Tests", "02.Fail.Tests", "03.Skip.Tests")]
    result = _run(workspace)
    assert result.returncode == 17
    assert (workspace / "dotnet.log").read_text(encoding="utf-8").splitlines() == [
        f"test|{project}|--nologo|--configuration|Debug|" for project in projects[:2]
    ]


def test_runner_fails_when_no_test_projects_are_found(workspace: Path) -> None:
    result = _run(workspace)
    assert result.returncode == 1
    assert "No .NET test projects were found" in result.stderr
    assert not (workspace / "dotnet.log").exists()


def test_runner_passes_release_and_no_restore_options_to_dotnet(workspace: Path) -> None:
    project = _project(workspace, "backend-dotnet/Only.Tests/Only.Tests.csproj")
    result = _run(workspace, "-Configuration", "Release", "-NoRestore")
    assert result.returncode == 0, result.stdout + result.stderr
    assert (workspace / "dotnet.log").read_text(encoding="utf-8").splitlines() == [
        f"test|{project}|--nologo|--configuration|Release|--no-restore",
    ]


def test_runner_fails_when_dotnet_cannot_be_started(workspace: Path) -> None:
    _project(workspace, "backend-dotnet/Only.Tests/Only.Tests.csproj")
    result = _run(workspace, missing_dotnet=True)
    assert result.returncode != 0
    assert "Unable to start dotnet" in result.stderr


def test_runner_rejects_invalid_root_instead_of_running_partial_matrix(tmp_path: Path) -> None:
    _project(tmp_path, "backend-dotnet/Only.Tests/Only.Tests.csproj")
    (tmp_path / "gui-winui3.Tests").write_text("not a directory", encoding="utf-8")
    result = _run(tmp_path)
    assert result.returncode != 0
    assert "Missing test root" in result.stderr
    assert not (tmp_path / "dotnet.log").exists()


def test_discovery_error_cannot_be_silently_ignored(workspace: Path) -> None:
    _project(workspace, "gui-winui3.Tests/gui-winui3.Tests.csproj")
    result = _run(workspace, discovery_error=True)
    assert result.returncode != 0
    assert "injected discovery failure" in result.stderr
    assert not (workspace / "dotnet.log").exists()


@pytest.mark.skipif(os.name != "nt", reason="named kernel mutexes are a Windows primitive")
def test_runner_refuses_to_start_while_the_named_lock_is_held(workspace: Path) -> None:
    mutex_name = f"ru-test-runner-held-{uuid4().hex}"
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    handle = kernel32.CreateMutexW(None, True, mutex_name)
    assert handle, ctypes.get_last_error()
    try:
        result = _run(workspace, mutex_name=mutex_name)
    finally:
        kernel32.CloseHandle(handle)

    assert result.returncode == 75, result.stdout + result.stderr
    assert "Another test matrix is already running" in result.stderr
    assert not (workspace / "dotnet.log").exists()
