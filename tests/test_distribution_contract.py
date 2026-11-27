"""Explicit ownership boundaries for distribution tooling."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_installers_do_not_close_apps_or_delete_the_profile() -> None:
    setup = (ROOT / "installer/Ru.iss").read_text(encoding="utf-8")
    assert "PrivilegesRequired=lowest" in setup
    assert "CloseApplications=no" in setup
    assert "RestartApplications=no" in setup
    assert "[UninstallDelete]" not in setup
    assert "[InstallDelete]" not in setup
    assert "ChangeUserPath(True)" in setup
    # Each product installs into a folder that names it, not a bare "Desktop" or "CLI".
    assert r"DefaultDirName={localappdata}\Programs\Ru Agent {#Product}" in setup
    assert "AppName=Ru Agent {#Product}" in setup
    helper =(ROOT / "installer/apply-update.ps1").read_text(encoding="utf-8")
    assert "Stop-Process" not in helper
    assert "taskkill" not in helper.lower()
    assert "/NOCLOSEAPPLICATIONS" in helper
    assert "Get-FileHash" in helper


def test_release_marker_is_compact_json() -> None:
    package = (ROOT / "scripts/package_release.ps1").read_text(encoding="utf-8")
    assert "ConvertTo-Json -Compress" in package


def test_installer_product_check_ignores_marker_whitespace() -> None:
    setup = (ROOT / "installer/Ru.iss").read_text(encoding="utf-8")
    assert "CompactMarker" in setup
    assert "StringChangeEx(CompactMarker, ' ', '', True)" in setup
    assert "StringChangeEx(CompactMarker, #9, '', True)" in setup
    assert "StringChangeEx(CompactMarker, #13, '', True)" in setup
    assert "StringChangeEx(CompactMarker, #10, '', True)" in setup
    assert "'\"Product\":\"{#LowerCase(Product)}\"'" in setup


def test_native_installer_smoke_is_restricted_to_disposable_ci() -> None:
    smoke = (ROOT / "scripts/installer_smoke.ps1").read_text(encoding="utf-8")
    assert "$env:GITHUB_ACTIONS -ne 'true'" in smoke
    assert "$env:CI -ne 'true'" in smoke
    assert "Uninstall did not restore the original user PATH" in smoke
    workflow = (ROOT / ".github/workflows/release.yml").read_text(encoding="utf-8")
    assert "installer_smoke.ps1" in workflow
    assert "test_dotnet.ps1" in workflow
    assert "package_release.ps1" in workflow
    assert "'--draft'" in workflow
    assert "continue-on-error" not in workflow


def test_updater_has_no_provider_or_execution_authority_dependency() -> None:
    project = (ROOT / "backend-dotnet/RuAgent.Updates/RuAgent.Updates.csproj").read_text(encoding="utf-8")
    assert "ProjectReference" not in project
    assert "PackageReference" not in project
    client = (ROOT / "backend-dotnet/RuAgent.Updates/ReleaseUpdateClient.cs").read_text(encoding="utf-8")
    assert "OniAntou/Ru-Agent" in client
    assert "AllowAutoRedirect = false" in client
    assert "Authorization" not in client
