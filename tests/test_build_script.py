"""Behavioral tests for the Windows release build script."""

from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path

import pytest


def _write_stub_commands(
    root: Path, *, gui_publish_exists: bool = True, missing_artifact: str = "",
) -> None:
    bin_dir = root / "bin"
    bin_dir.mkdir()
    gui_publish = (
        "  mkdir \"%RU_BUILD_TEST_ROOT%\\gui-winui3\\bin\\Release\\net10.0-windows10.0.26100.0\\win-x64\\publish\" >nul 2>&1\n"
        "  echo gui > \"%RU_BUILD_TEST_ROOT%\\gui-winui3\\bin\\Release\\net10.0-windows10.0.26100.0\\win-x64\\publish\\RuApp.exe\"\n"
        if gui_publish_exists
        else ""
    )
    omitted_path = {
        "worker": r"backend-dotnet\RuAgent.Runtime.Worker\bin\Release\net10.0\win-x64\publish\RuAgent.Runtime.Worker.exe",
        "core": r"backend-dotnet\RuAgent.Runtime.Worker\bin\Release\net10.0\win-x64\publish\RuAgent.Runtime.Core.dll",
        "runtimeconfig": r"gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.runtimeconfig.json",
    }.get(missing_artifact)
    omit_artifact = (
        f'  del /q "%RU_BUILD_TEST_ROOT%\\{omitted_path}" >nul 2>&1\n'
        if omitted_path else ""
    )
    (bin_dir / "dotnet.cmd").write_text(
        "@echo off\n"
        ">>\"%RU_BUILD_TEST_ROOT%\\dotnet.invocations\" echo %*\n"
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
        f"{omit_artifact}"
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
    missing_artifact: str = "",
    missing_xaml: str = "",
    with_overlay: bool = False,
) -> subprocess.CompletedProcess[str]:
    repo_root = Path(__file__).resolve().parents[1]
    build_root = tmp_path / "checkout"
    build_root.mkdir()
    shutil.copy2(repo_root / "build.bat", build_root / "build.bat")
    (build_root / "gui-winui3").mkdir()
    (build_root / "gui-winui3" / "RuApp.csproj").write_text("stub\n", encoding="utf-8")
    compiled_xaml = build_root / "gui-winui3/obj/x64/Release/net10.0-windows10.0.26100.0/win-x64"
    for relative in ("App.xbf", "MainWindow.xbf", "Views/ChatView.xbf"):
        if relative == missing_xaml:
            continue
        artifact = compiled_xaml / relative
        artifact.parent.mkdir(parents=True, exist_ok=True)
        artifact.write_text(f"compiled {relative}\n", encoding="utf-8")
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

    _write_stub_commands(tmp_path, gui_publish_exists=gui_publish_exists, missing_artifact=missing_artifact)
    env = os.environ.copy()
    env["PATH"] = f"{tmp_path / 'bin'}{os.pathsep}{env['PATH']}"
    env["RU_BUILD_TEST_ROOT"] = str(build_root)
    arguments = ["cmd", "/d", "/c", str(build_root / "build.bat")]
    if with_overlay:
        arguments.append("-WithOverlay")
    return subprocess.run(
        arguments,
        cwd=build_root,
        env=env,
        text=True,
        capture_output=True,
        check=False,
        timeout=30,
    )


def test_overlay_plugin_is_skipped_by_default_and_core_build_still_succeeds(
    tmp_path: Path,
) -> None:
    result = _run_build_script(tmp_path, overlay_exists=True)

    assert result.returncode == 0, result.stdout + result.stderr
    assert "Build complete" in result.stdout
    assert "SKIPPED" in result.stdout
    assert "Overlay plugin:      skipped" in result.stdout
    assert not (tmp_path / "checkout" / "powershell.invocations").exists()


def test_broken_overlay_toolchain_cannot_fail_the_default_core_build(
    tmp_path: Path,
) -> None:
    result = _run_build_script(tmp_path, overlay_exists=True, native_failure=True)

    assert result.returncode == 0, result.stdout + result.stderr
    assert "Build complete" in result.stdout
    assert not (tmp_path / "checkout" / "powershell.invocations").exists()


def test_with_overlay_flag_builds_the_plugin(tmp_path: Path) -> None:
    result = _run_build_script(tmp_path, overlay_exists=True, with_overlay=True)

    assert result.returncode == 0, result.stdout + result.stderr
    invocations = (tmp_path / "checkout" / "powershell.invocations").read_text(
        encoding="utf-8"
    )
    assert "build_ru_desktop_overlay.ps1" in invocations
    assert "Overlay plugin:      built" in result.stdout


def test_existing_overlay_still_runs_incremental_native_build(tmp_path: Path) -> None:
    result = _run_build_script(tmp_path, overlay_exists=True, with_overlay=True)

    assert result.returncode == 0, result.stdout + result.stderr
    invocations = (tmp_path / "checkout" / "powershell.invocations").read_text(
        encoding="utf-8"
    )
    assert "build_ru_desktop_overlay.ps1" in invocations
    assert "cmake --build" in (
        tmp_path / "checkout" / "cmake.invocations"
    ).read_text(encoding="utf-8")
    commands = (tmp_path / "checkout/dotnet.invocations").read_text(encoding="utf-8").splitlines()
    publishes = [command for command in commands if command.startswith("publish ")]
    worker = next(i for i, command in enumerate(publishes) if "RuAgent.Runtime.Worker.csproj" in command)
    backend = next(i for i, command in enumerate(publishes) if "RuBackend.csproj" in command)
    assert worker < backend
    gui = next(command for command in publishes if "RuApp.csproj" in command)
    assert all(flag in gui.split() for flag in (
        "-p:Platform=x64", "-p:RuntimeIdentifier=win-x64", "-p:SelfContained=true",
    ))


@pytest.mark.parametrize("missing_artifact", ["worker", "core", "runtimeconfig"])
def test_missing_runtime_publish_artifact_stops_release_before_native_build(
    tmp_path: Path, missing_artifact: str,
) -> None:
    result = _run_build_script(
        tmp_path, overlay_exists=True, missing_artifact=missing_artifact, with_overlay=True
    )
    assert result.returncode != 0
    expected_diagnostic = {
        "worker": "RuAgent.Runtime.Worker.exe was not created.",
        "core": "RuAgent.Runtime.Core.dll was not published.",
        "runtimeconfig": "RuApp.runtimeconfig.json was not created.",
    }[missing_artifact]
    assert expected_diagnostic in result.stdout
    assert not (tmp_path / "checkout/powershell.invocations").exists()


def test_native_build_failure_stops_release_build_when_plugin_is_enabled(tmp_path: Path) -> None:
    result = _run_build_script(tmp_path, overlay_exists=True, native_failure=True, with_overlay=True)

    assert result.returncode != 0
    assert "Build complete" not in result.stdout


def test_release_copies_x64_compiled_xaml(tmp_path: Path) -> None:
    result = _run_build_script(tmp_path, overlay_exists=True)
    assert result.returncode == 0, result.stdout + result.stderr
    assert "view XAML files." in result.stdout
    publish = tmp_path / "checkout/gui-winui3/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish"
    for relative in ("App.xbf", "MainWindow.xbf", "Views/ChatView.xbf"):
        artifact = publish / relative
        assert artifact.is_file(), f"Missing published XAML: {relative}"
        assert artifact.read_text(encoding="utf-8") == f"compiled {relative}\n"


@pytest.mark.parametrize("missing_xaml", ["App.xbf", "MainWindow.xbf", "Views/ChatView.xbf"])
def test_missing_compiled_xaml_stops_release(tmp_path: Path, missing_xaml: str) -> None:
    result = _run_build_script(tmp_path, overlay_exists=True, missing_xaml=missing_xaml, with_overlay=True)
    assert result.returncode != 0
    assert "Build complete" not in result.stdout
    assert "was not created" in result.stdout
    assert not (tmp_path / "checkout/powershell.invocations").exists()


def test_missing_gui_publish_fails_before_overlay_build(tmp_path: Path) -> None:
    result = _run_build_script(
        tmp_path,
        overlay_exists=True,
        gui_publish_exists=False,
        with_overlay=True,
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


def test_start_dev_passes_uncorrupted_working_directory_to_launcher(tmp_path: Path) -> None:
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
    worker = (
        tmp_path
        / "backend-dotnet"
        / "RuAgent.Runtime.Worker"
        / "bin"
        / "Release"
        / "net10.0"
        / "win-x64"
        / "publish"
    )
    worker.mkdir(parents=True)
    (worker / "RuAgent.Runtime.Worker.exe").write_text("worker placeholder\n", encoding="utf-8")
    scripts = tmp_path / "scripts"
    scripts.mkdir()
    (scripts / "launch_gui.ps1").write_text(
        "param([string]$ExecutablePath, [string]$WorkingDirectory, "
        "[int]$StartupGracePeriodMilliseconds = 2000)\n"
        "Set-Content -LiteralPath (Join-Path $PSScriptRoot 'launcher-args.txt') "
        "-Value $WorkingDirectory\n"
        "exit 0\n",
        encoding="utf-8",
    )

    result = subprocess.run(
        ["cmd", "/d", "/c", str(script)],
        cwd=tmp_path,
        text=True,
        capture_output=True,
        check=False,
    )

    assert result.returncode == 0, result.stdout + result.stderr
    working_directory = (scripts / "launcher-args.txt").read_text(encoding="utf-8").strip()
    assert '"' not in working_directory, working_directory
    assert Path(working_directory).resolve() == tmp_path.resolve()


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
