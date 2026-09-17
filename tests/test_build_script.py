"""Behavioral tests for the Windows release build script."""

from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path

import pytest


def _write_stub_commands(
    root: Path, *, gui_publish_exists: bool = True, missing_artifact: str = "",
    missing_runtime: str = "",
) -> None:
    bin_dir = root / "bin"
    bin_dir.mkdir()
    gui_publish = (
        "  mkdir \"%RU_BUILD_TEST_ROOT%\\gui-winui3\\bin\\Release\\net10.0-windows10.0.26100.0\\win-x64\\publish\" >nul 2>&1\n"
        "  echo gui > \"%RU_BUILD_TEST_ROOT%\\gui-winui3\\bin\\Release\\net10.0-windows10.0.26100.0\\win-x64\\publish\\RuApp.exe\"\n"
        "  echo runtime > \"%RU_BUILD_TEST_ROOT%\\gui-winui3\\bin\\Release\\net10.0-windows10.0.26100.0\\win-x64\\publish\\Microsoft.WindowsAppRuntime.dll\"\n"
        "  echo xaml > \"%RU_BUILD_TEST_ROOT%\\gui-winui3\\bin\\Release\\net10.0-windows10.0.26100.0\\win-x64\\publish\\Microsoft.UI.Xaml.dll\"\n"
        "  echo dwrite > \"%RU_BUILD_TEST_ROOT%\\gui-winui3\\bin\\Release\\net10.0-windows10.0.26100.0\\win-x64\\publish\\DWriteCore.dll\"\n"
        "  echo mrm > \"%RU_BUILD_TEST_ROOT%\\gui-winui3\\bin\\Release\\net10.0-windows10.0.26100.0\\win-x64\\publish\\MRM.dll\"\n"
        if gui_publish_exists
        else ""
    )
    # A framework-dependent publish carries the bootstrap shim instead of the
    # runtime, which is the failure the close-out check exists to catch.
    if missing_runtime:
        gui_publish += (
            f'  del /q "%RU_BUILD_TEST_ROOT%\\gui-winui3\\bin\\Release\\net10.0-windows10.0.26100.0'
            f'\\win-x64\\publish\\{missing_runtime}" >nul 2>&1\n'
        )
    omitted_path = {
        "worker": r"backend-dotnet\RuAgent.Runtime.Worker\bin\Release\net10.0\win-x64\publish\RuAgent.Runtime.Worker.exe",
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
        "exit /b 0\n",
        encoding="utf-8",
    )


def _run_build_script(
    tmp_path: Path,
    *,
    gui_publish_exists: bool = True,
    missing_artifact: str = "",
    missing_xaml: str = "",
    missing_runtime: str = "",
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

    _write_stub_commands(
        tmp_path,
        gui_publish_exists=gui_publish_exists,
        missing_artifact=missing_artifact,
        missing_runtime=missing_runtime,
    )
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
        timeout=30,
    )


def test_release_build_completes_without_optional_plugins(tmp_path: Path) -> None:
    result = _run_build_script(tmp_path)

    assert result.returncode == 0, result.stdout + result.stderr
    assert "Build complete" in result.stdout
    assert not (tmp_path / "checkout" / "powershell.invocations").exists()


def test_publish_order_and_self_contained_gui_flags(tmp_path: Path) -> None:
    result = _run_build_script(tmp_path)

    assert result.returncode == 0, result.stdout + result.stderr
    commands = (tmp_path / "checkout/dotnet.invocations").read_text(encoding="utf-8").splitlines()
    publishes = [command for command in commands if command.startswith("publish ")]
    worker = next(i for i, command in enumerate(publishes) if "RuAgent.Runtime.Worker.csproj" in command)
    backend = next(i for i, command in enumerate(publishes) if "RuBackend.csproj" in command)
    assert worker < backend
    gui = next(command for command in publishes if "RuApp.csproj" in command)
    assert all(flag in gui.split() for flag in (
        "-p:Platform=x64", "-p:RuntimeIdentifier=win-x64", "-p:SelfContained=true",
    ))


@pytest.mark.parametrize("missing_artifact", ["worker", "runtimeconfig"])
def test_missing_runtime_publish_artifact_stops_release_build(
    tmp_path: Path, missing_artifact: str,
) -> None:
    result = _run_build_script(tmp_path, missing_artifact=missing_artifact)
    assert result.returncode != 0
    expected_diagnostic = {
        "worker": "RuAgent.Runtime.Worker.exe was not created.",
        "runtimeconfig": "RuApp.runtimeconfig.json was not created.",
    }[missing_artifact]
    assert expected_diagnostic in result.stdout
    assert not (tmp_path / "checkout/powershell.invocations").exists()


def test_release_copies_x64_compiled_xaml(tmp_path: Path) -> None:
    result = _run_build_script(tmp_path)
    assert result.returncode == 0, result.stdout + result.stderr
    assert "view XAML files." in result.stdout
    publish = tmp_path / "checkout/gui-winui3/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish"
    for relative in ("App.xbf", "MainWindow.xbf", "Views/ChatView.xbf"):
        artifact = publish / relative
        assert artifact.is_file(), f"Missing published XAML: {relative}"
        assert artifact.read_text(encoding="utf-8") == f"compiled {relative}\n"


def test_release_flattens_every_executable_into_one_directory(tmp_path: Path) -> None:
    """A distribution is one directory. Without this stage the published GUI only
    worked from a checkout, because the launcher resolved its siblings by
    repository layout."""
    result = _run_build_script(tmp_path)
    assert result.returncode == 0, result.stdout + result.stderr
    assert "Flattened:" in result.stdout

    publish = tmp_path / "checkout/gui-winui3/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish"
    for executable in (
        "RuApp.exe",
        "RuBackend.exe",
        "RuAgent.Runtime.Worker.exe",
        "RuAgent.Broker.exe",
        "RuAgent.Cli.exe",
    ):
        assert (publish / executable).is_file(), f"Missing from the flat layout: {executable}"


def test_flat_layout_stage_does_not_copy_the_destination_into_itself(tmp_path: Path) -> None:
    """xcopy refuses a cyclic copy, so the GUI publish directory must not appear
    among the sources: it already is the destination."""
    result = _run_build_script(tmp_path)

    assert result.returncode == 0, result.stdout + result.stderr
    assert "cyclic copy" not in result.stdout
    assert "cyclic copy" not in result.stderr


def test_release_reports_the_windows_app_runtime_as_bundled(tmp_path: Path) -> None:
    result = _run_build_script(tmp_path)

    assert result.returncode == 0, result.stdout + result.stderr
    assert "Windows App Runtime bundled" in result.stdout


@pytest.mark.parametrize(
    "missing_runtime",
    ["Microsoft.WindowsAppRuntime.dll", "Microsoft.UI.Xaml.dll", "DWriteCore.dll", "MRM.dll"],
)
def test_framework_dependent_gui_stops_the_release_build(
    tmp_path: Path, missing_runtime: str,
) -> None:
    """These files only appear when WindowsAppSDKSelfContained is in effect. A
    publish without them needs the Windows App Runtime installed on the target
    machine, which is exactly what the release must not require."""
    result = _run_build_script(tmp_path, missing_runtime=missing_runtime)

    assert result.returncode != 0
    assert "would need the Windows App Runtime installed" in result.stdout
    assert "WindowsAppSDKSelfContained" in result.stdout
    assert "Build complete" not in result.stdout


def test_gui_project_ships_the_windows_app_runtime_self_contained() -> None:
    """The build check fails if this property is dropped; this makes the intent
    explicit where someone would look to change it."""
    repo_root = Path(__file__).resolve().parents[1]
    project = (repo_root / "gui-winui3" / "RuApp.csproj").read_text(encoding="utf-8")

    assert "<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>" in project


@pytest.mark.parametrize("missing_xaml", ["App.xbf", "MainWindow.xbf", "Views/ChatView.xbf"])
def test_missing_compiled_xaml_stops_release(tmp_path: Path, missing_xaml: str) -> None:
    result = _run_build_script(tmp_path, missing_xaml=missing_xaml)
    assert result.returncode != 0
    assert "Build complete" not in result.stdout
    assert "was not created" in result.stdout
    assert not (tmp_path / "checkout/powershell.invocations").exists()


def test_missing_gui_publish_fails_the_release_build(tmp_path: Path) -> None:
    result = _run_build_script(tmp_path, gui_publish_exists=False)

    assert result.returncode != 0
    assert "RuApp.exe was not created" in result.stdout
    assert "Build complete" not in result.stdout


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
