"""Behavioral tests for the Windows release build script."""

from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path


def _write_stub_commands(root: Path, *, gui_publish_exists: bool = True) -> None:
    bin_dir = root / "bin"
    bin_dir.mkdir()
    gui_publish = (
        "  mkdir \"%RU_BUILD_TEST_ROOT%\\gui-winui3\\bin\\Release\\net10.0-windows10.0.26100.0\\win-x64\\publish\" >nul 2>&1\n"
        "  echo gui > \"%RU_BUILD_TEST_ROOT%\\gui-winui3\\bin\\Release\\net10.0-windows10.0.26100.0\\win-x64\\publish\\RuApp.exe\"\n"
        if gui_publish_exists
        else ""
    )
    (bin_dir / "dotnet.cmd").write_text(
        "@echo off\n"
        "if /I \"%~1\"==\"publish\" (\n"
        "  mkdir \"%RU_BUILD_TEST_ROOT%\\backend-dotnet\\RuAgent.Broker\\bin\\Release\\net10.0\\win-x64\\publish\" >nul 2>&1\n"
        "  mkdir \"%RU_BUILD_TEST_ROOT%\\backend-dotnet\\RuBackend\\bin\\Release\\net10.0\\win-x64\\publish\" >nul 2>&1\n"
        "  mkdir \"%RU_BUILD_TEST_ROOT%\\backend-dotnet\\RuAgent.Cli\\bin\\Release\\net10.0\\win-x64\\publish\" >nul 2>&1\n"
        "  mkdir \"%RU_BUILD_TEST_ROOT%\\backend-dotnet\\RuAgent.Runtime.Worker\\bin\\Release\\net10.0\\win-x64\\publish\" >nul 2>&1\n"
        "  echo broker > \"%RU_BUILD_TEST_ROOT%\\backend-dotnet\\RuAgent.Broker\\bin\\Release\\net10.0\\win-x64\\publish\\RuAgent.Broker.exe\"\n"
        "  echo backend > \"%RU_BUILD_TEST_ROOT%\\backend-dotnet\\RuBackend\\bin\\Release\\net10.0\\win-x64\\publish\\RuBackend.exe\"\n"
        "  echo cli > \"%RU_BUILD_TEST_ROOT%\\backend-dotnet\\RuAgent.Cli\\bin\\Release\\net10.0\\win-x64\\publish\\RuAgent.Cli.exe\"\n"
        "  echo worker > \"%RU_BUILD_TEST_ROOT%\\backend-dotnet\\RuAgent.Runtime.Worker\\bin\\Release\\net10.0\\win-x64\\publish\\RuAgent.Runtime.Worker.exe\"\n"
        "  echo runtime-core > \"%RU_BUILD_TEST_ROOT%\\backend-dotnet\\RuAgent.Runtime.Worker\\bin\\Release\\net10.0\\win-x64\\publish\\RuAgent.Runtime.Core.dll\"\n"
        f"{gui_publish}"
        "  echo {\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}]}} > \"%RU_BUILD_TEST_ROOT%\\gui-winui3\\bin\\Release\\net10.0-windows10.0.26100.0\\win-x64\\publish\\RuApp.runtimeconfig.json\"\n"
        ")\n"
        "exit /b 0\n",
        encoding="utf-8",
    )
    (bin_dir / "powershell.cmd").write_text(
        "@echo off\n"
        ">>\"%RU_BUILD_TEST_ROOT%\\powershell.invocations\" echo %*\n"
        ">>\"%RU_BUILD_TEST_ROOT%\\cmake.invocations\" echo cmake --build out\\build --config Release --target test\n"
        "if exist \"%RU_BUILD_TEST_ROOT%\\powershell.fail\" exit /b 7\n"
        "mkdir \"%RU_BUILD_TEST_ROOT%\\tools\\ru_desktop_overlay\\out\\build\\Release\" >nul 2>&1\n"
        "echo overlay > \"%RU_BUILD_TEST_ROOT%\\tools\\ru_desktop_overlay\\out\\build\\Release\\RuDesktopOverlay.exe\"\n"
        "exit /b 0\n",
        encoding="utf-8",
    )


def _run_build_script(
    tmp_path: Path,
    *,
    overlay_exists: bool,
    native_failure: bool = False,
    gui_publish_exists: bool = True,
) -> subprocess.CompletedProcess[str]:
    repo_root = Path(__file__).resolve().parents[1]
    build_root = tmp_path / "checkout"
    build_root.mkdir()
    shutil.copy2(repo_root / "build.bat", build_root / "build.bat")
    (build_root / "gui-winui3").mkdir()
    (build_root / "gui-winui3" / "RuApp.csproj").write_text("stub\n", encoding="utf-8")
    (build_root / "backend-dotnet" / "RuAgent.Broker").mkdir(parents=True)
    (build_root / "backend-dotnet" / "RuBackend").mkdir(parents=True)
    (build_root / "backend-dotnet" / "RuAgent.Runtime.Worker").mkdir(parents=True)
    (build_root / "backend-dotnet" / "RuAgent.Broker" / "RuAgent.Broker.csproj").write_text(
        "stub\n", encoding="utf-8"
    )
    (build_root / "backend-dotnet" / "RuBackend" / "RuBackend.csproj").write_text(
        "stub\n", encoding="utf-8"
    )
    (build_root / "backend-dotnet" / "RuAgent.Runtime.Worker" / "RuAgent.Runtime.Worker.csproj").write_text(
        "stub\n", encoding="utf-8"
    )
    (build_root / "scripts").mkdir()
    (build_root / "scripts" / "build_ru_desktop_overlay.ps1").write_text(
        "# stub\n", encoding="utf-8"
    )
    if overlay_exists:
        overlay = build_root / "tools" / "ru_desktop_overlay" / "out" / "build" / "Release"
        overlay.mkdir(parents=True)
        (overlay / "RuDesktopOverlay.exe").write_text("pre-existing\n", encoding="utf-8")
    if native_failure:
        (build_root / "powershell.fail").write_text("fail\n", encoding="utf-8")

    _write_stub_commands(tmp_path, gui_publish_exists=gui_publish_exists)
    env = os.environ.copy()
    env["PATH"] = f"{tmp_path / 'bin'}{os.pathsep}{env['PATH']}"
    env["RU_BUILD_TEST_ROOT"] = str(build_root)
    return subprocess.run(
        ["cmd", "/d", "/c", str(build_root / "build.bat")],
        cwd=build_root,
        env=env,
        text=True,
        capture_output=True,
        check=False,
    )


def test_existing_overlay_still_runs_incremental_native_build(tmp_path: Path) -> None:
    result = _run_build_script(tmp_path, overlay_exists=True)

    assert result.returncode == 0, result.stdout + result.stderr
    invocations = (tmp_path / "checkout" / "powershell.invocations").read_text(
        encoding="utf-8"
    )
    assert "build_ru_desktop_overlay.ps1" in invocations
    assert "cmake --build" in (
        tmp_path / "checkout" / "cmake.invocations"
    ).read_text(encoding="utf-8")
    assert "[1/13]" in result.stdout
    assert "[13/13]" in result.stdout
    assert "RuAgent.Cli" in result.stdout
    assert "RuAgent.Harness.Core" in result.stdout


def test_native_build_failure_stops_release_build(tmp_path: Path) -> None:
    result = _run_build_script(tmp_path, overlay_exists=True, native_failure=True)

    assert result.returncode != 0
    assert "Build complete" not in result.stdout


def test_missing_gui_publish_fails_before_overlay_build(tmp_path: Path) -> None:
    result = _run_build_script(
        tmp_path,
        overlay_exists=True,
        gui_publish_exists=False,
    )

    assert result.returncode != 0
    assert "RuApp.exe was not created" in result.stdout
    assert "Build complete" not in result.stdout
    assert not (tmp_path / "checkout" / "powershell.invocations").exists()


def test_start_dev_rejects_debug_fallback_when_release_is_missing(tmp_path: Path) -> None:
    repo_root = Path(__file__).resolve().parents[1]
    script = tmp_path / "start_dev.bat"
    shutil.copy2(repo_root / "start_dev.bat", script)
    debug = (
        tmp_path
        / "gui-winui3"
        / "bin"
        / "Debug"
        / "net10.0-windows10.0.26100.0"
        / "win-x64"
        / "RuApp.exe"
    )
    debug.parent.mkdir(parents=True)
    debug.write_text("debug placeholder\n", encoding="utf-8")

    result = subprocess.run(
        ["cmd", "/d", "/c", str(script)],
        cwd=tmp_path,
        text=True,
        capture_output=True,
        check=False,
    )

    assert result.returncode != 0
    assert "Release publish" in result.stdout
    assert "Debug" not in result.stdout


def test_start_dev_requires_the_managed_worker_publish(tmp_path: Path) -> None:
    repo_root = Path(__file__).resolve().parents[1]
    script = tmp_path / "start_dev.bat"
    shutil.copy2(repo_root / "start_dev.bat", script)
    release = (
        tmp_path
        / "gui-winui3"
        / "bin"
        / "Release"
        / "net10.0-windows10.0.26100.0"
        / "win-x64"
        / "publish"
    )
    release.mkdir(parents=True)
    (release / "RuApp.exe").write_text("release placeholder\n", encoding="utf-8")

    result = subprocess.run(
        ["cmd", "/d", "/c", str(script)],
        cwd=tmp_path,
        text=True,
        capture_output=True,
        check=False,
    )

    assert result.returncode != 0
    assert "Managed Worker publish output not found" in result.stdout
