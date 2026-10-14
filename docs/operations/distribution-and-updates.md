# Windows distributions and updates

Ru targets Windows 10 build 17763 or newer on x64. Desktop and CLI are separate
products with their own matching, self-contained Host. Neither requires a .NET
SDK on the user's computer. Source builds still use `build.bat`.

## Install

Download `Ru-Desktop-VERSION-win-x64-Setup.exe` or
`Ru-CLI-VERSION-win-x64-Setup.exe` from the official
[releases](https://github.com/OniAntou/Ru-Agent/releases). These asset names become
available when the first release is published; packaging code alone does not
publish them. Default installation roots are `%LOCALAPPDATA%\Programs\Ru\Desktop`
and `%LOCALAPPDATA%\Programs\Ru\CLI`, without administrator rights. Use a
dedicated empty directory for a new installation. Desktop adds a Start-menu
shortcut; CLI adds only its own directory to the user's PATH. Open a new terminal
and run `ru` after installing CLI. ZIP archives provide the matching portable trees.

If `ru` already points to a source checkout or another installation, use `where ru`
to select the intended command. The installer preserves other PATH entries.

For CLI command-based installation after release publication, download and inspect
`scripts/install.ps1` from this repository, then run it with Windows PowerShell:

```powershell
Invoke-WebRequest https://raw.githubusercontent.com/OniAntou/Ru-Agent/dev/scripts/install.ps1 -OutFile .\install.ps1
# Inspect install.ps1 before running it.
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -Channel Preview
```

The bootstrap selects a compatible release, bounds its download and checks its
GitHub-provided SHA-256 digest. It fails if no matching release exists. It never
fetches from an arbitrary model-provided URL.

## Update

In Desktop, open **Settings → General → Updates**, choose Stable or Preview,
then **Check for updates**. **Install and restart** downloads with progress and
cancellation, verifies the asset and exits before replacing files. In a terminal:

```text
ru update --check
ru update --channel preview
ru update --yes
ru --version
```

Inside TUI, `/update` checks without reading from its terminal editor;
`/update --yes` explicitly installs and exits. Stable is the default shared local
preference. Preview includes stable releases and prereleases; choosing Stable
does not downgrade an installed preview. No background installation happens.

Updates refuse while there is an active request or any unfinished run, including
approval/input waits. Finish or cancel those runs first. The helper waits for the
calling process and idle Host to release files; it does not kill another Ru
window. Close other windows using that product's files if installation reports a
lock. Different installed frontend versions cannot attach to the same Host;
`host_version_mismatch` asks you to close the other application.

Sessions, workspace state, settings and credential storage remain in the canonical
Ru profile, outside both installation roots. Uninstall removes application-owned
files and the CLI's exact PATH entry, while retaining the profile. Obsolete files
listed by the previous package manifest are removed during a successful upgrade;
unlisted local files are retained.

## Recovery and release verification

`%LOCALAPPDATA%\Ru\updates\last-update.json` records success or failure;
Settings and `ru update` show the previous result. A failed handoff's private
staging directory retains `install.log` for diagnosis. Re-run the same official
installer if necessary. Updates do not guarantee rollback after interruption or
support downgrading a newer database with an older release. Back up the profile
before deliberate manual version rollback.

Build release artifacts with Inno Setup 6.2.2+ available:

```powershell
.\scripts\package_release.ps1 -Version 0.1.0-preview.1 -IsccPath C:\Tools\Inno\ISCC.exe
```

Output lives under `artifacts/releases/VERSION`: two Setup executables, two ZIPs,
SHA256SUMS.txt and the product staging trees. `-SkipBuild` accepts only publish
trees whose stamped version matches; it also rejects a desktop tree containing
the CLI frontend. The packaging script uses the same machine-wide build/test mutex.
The release workflow validates code, builds packages and runs install/update/uninstall
smoke checks on a disposable CI machine before creating a draft release.
Publishing the draft is a separate maintainer action. No package manager listing
or published release is implied by this workflow.

`scripts/installer_local_smoke.ps1` supplies a workspace-only native test variant:
a separate test AppId, no uninstall registration, shortcuts, PATH changes or GUI
launch. It checks file installation, lock refusal, helper handoff, owned-file
cleanup and uninstall. Its synthetic old marker exercises installer mechanics,
not compatibility with an older released binary. The production registration
and PATH checks run separately on disposable CI.

Local compile/download fixtures, real installer acceptance and live-provider
behavior are separate evidence. Local builds can be unsigned previews. Provision
an Authenticode identity and verify signing/SmartScreen behavior before claiming
a signed production installer; SHA-256 integrity does not supply publisher identity.
