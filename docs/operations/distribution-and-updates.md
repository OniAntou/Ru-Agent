# Windows distributions and updates

Ru targets Windows 10 build 17763 or newer on x64. Desktop and CLI are separate
products with their own matching, self-contained Host. Neither requires a .NET
SDK on the user's computer. Source builds still use `build.bat`.

## Install

Download `Ru-Desktop-VERSION-win-x64-Setup.exe` or
`Ru-CLI-VERSION-win-x64-Setup.exe` from the official
[releases](https://github.com/OniAntou/Ru-Agent/releases). The first public release
is [0.1.0-preview.1](https://github.com/OniAntou/Ru-Agent/releases/tag/v0.1.0-preview.1);
no Stable release is published yet. Default installation roots are `%LOCALAPPDATA%\Programs\Ru\Desktop`
and `%LOCALAPPDATA%\Programs\Ru\CLI`, without administrator rights. Use a
dedicated empty directory for a new installation. Desktop adds a Start-menu
shortcut; CLI adds only its own directory to the user's PATH. Open a new terminal
and run `ru` after installing CLI. ZIP archives provide the matching portable trees.

If `ru` already points to a source checkout or another installation, use `where ru`
to select the intended command. The installer preserves other PATH entries.

Install CLI with one command in Command Prompt (also works in PowerShell):

```cmd
powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/OniAntou/Ru-Agent/dev/scripts/install.ps1'))) -Channel Preview"
```

This runs the bootstrap from the official repository. To inspect it first, use
the manual alternative:

```powershell
Invoke-WebRequest https://raw.githubusercontent.com/OniAntou/Ru-Agent/dev/scripts/install.ps1 -OutFile .\install.ps1
# Inspect install.ps1 before running it.
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -Channel Preview
```

The script runs the verified installer silently and adds the CLI to the user
PATH. Omitting `-Channel Preview` selects Stable, which needs a published Stable
release. Open a new terminal after installation.

Interactive bootstrap downloads and `ru update` show a progress bar with
percent, transferred megabytes and average speed. Redirected output stays plain
text. The bar describes transfer progress; SHA-256 verification completes before
installation.

The bootstrap selects a compatible release, bounds its download and checks its
GitHub-provided SHA-256 digest. It fails if no matching release exists. It never
fetches from an arbitrary model-provided URL.

## Uninstall

`ru uninstall` opens the registered Windows CLI uninstaller after the caller
exits. Confirm in that wizard to remove CLI files and its user PATH entry.
Sessions, API keys and the shared Ru profile remain. It refuses unknown or
mismatched registrations and never removes the source checkout, a portable
folder or Desktop. Close other CLI windows before uninstalling; active runs
block the existing idle-only maintenance handshake. Failures are recorded in
`%LOCALAPPDATA%\Ru\updates\last-uninstall.json`.

This command is included in builds after `0.1.0-preview.1`; that original
release can be removed through **Windows Settings → Apps → Ru CLI → Uninstall**.
Portable users delete their extracted application folder themselves.

## Update

In Desktop, **Settings → About** shows the application version, build type,
Windows architecture and repository link alongside Updates. Installed releases
check the selected channel at startup and every 30 minutes while Ru is open.
Checks time out after 30 seconds and never download or install automatically.
Source builds keep their rebuild guidance and do not poll releases.

The title-bar **Update** button appears only after a newer compatible release
is found. It opens About without discarding that result. No result, channel
changes or failed checks hide it. You can always open
**Settings → About → Updates**, choose Stable or Preview,
then **Check for updates**. In the original `0.1.0-preview.1`, this section is
still under General; About and the conditional title-bar button are later
source changes. **Install and restart** downloads with progress and
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
CI downloads Inno Setup 6.5.3 from its official release, checks its pinned SHA-256
and publisher signature, and installs it only on that disposable runner.
CLI PATH maintenance preserves unrelated entries and their empty separators;
uninstall removes only the matching installation entry.
Publishing the draft is a separate maintainer action. No package manager listing
or published release is implied by this workflow.

`scripts/installer_local_smoke.ps1` supplies a workspace-only native test variant:
a separate test AppId, no uninstall registration, shortcuts, PATH changes or GUI
launch. It checks file installation, lock refusal, helper handoff, owned-file
cleanup and uninstall. Its synthetic old marker exercises installer mechanics,
not compatibility with an older released binary. The production registration
and PATH checks run separately on disposable CI.
The local CLI variant also runs pure string cases for PATH append/remove,
including quoted entries, empty separators and a missing installation entry.

Local compile/download fixtures, real installer acceptance and live-provider
behavior are separate evidence. Local builds can be unsigned previews. Provision
an Authenticode identity and verify signing/SmartScreen behavior before claiming
a signed production installer; SHA-256 integrity does not supply publisher identity.
