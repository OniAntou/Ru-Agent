# Install Ru

Ru runs on Windows 10 (build 17763) or newer, x64. It ships as two separate products: the **desktop
app** and the **CLI**, which is the terminal UI. Each carries its own matching backend, so you do not
need the .NET SDK unless you build from source.

> [!IMPORTANT]
> Ru is a Preview and the initial installers are unsigned, so Windows may warn before running them. No
> Stable release is published yet; the CLI command below selects the Preview channel for that reason.

## Install the desktop app

1. Open the [Releases page](https://github.com/OniAntou/Ru-Agent/releases) and download
   `Ru-Desktop-VERSION-win-x64-Setup.exe`.
2. Run it. It installs for your user under `%LOCALAPPDATA%\Programs\Ru\Desktop` without administrator
   rights and adds a Start-menu shortcut.
3. Start **Ru** from the Start menu.

## Install the terminal UI

1. Open Command Prompt. PowerShell works too.
2. Run the installer:

    ```cmd
    powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/OniAntou/Ru-Agent/dev/scripts/install.ps1'))) -Channel Preview"
    ```

3. Open a new terminal and run `ru`.

The script downloads a compatible release, checks its SHA-256 digest, runs the installer silently and
adds the CLI to your user `PATH`. It never fetches from a URL a model supplies. To read the script
before running it:

```powershell
Invoke-WebRequest https://raw.githubusercontent.com/OniAntou/Ru-Agent/dev/scripts/install.ps1 -OutFile .\install.ps1
# Inspect install.ps1 before running it.
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -Channel Preview
```

You can also download `Ru-CLI-VERSION-win-x64-Setup.exe` or a portable ZIP from the Releases page. If
`ru` starts a different copy than you expect, such as a source checkout, `where ru` shows which one runs.

## Where Ru keeps your data

Sessions, settings and workspace state live in your Ru profile under `%LOCALAPPDATA%\Ru`, outside the
install folders. API keys are stored in Windows Credential Manager. Updating or uninstalling the
application leaves the profile and your keys in place.

## Keep it up to date

- **Desktop:** open **Settings → About → Updates**, choose Stable or Preview, then **Check for updates**.
  Ru also checks at startup and every 30 minutes while it is open, but it never downloads or installs
  anything on its own. **Install and restart** does the update with a progress bar you can cancel.
- **Terminal:**

    ```text
    ru update --check
    ru update --channel preview
    ru update --yes
    ru --version
    ```

    Inside the terminal UI, `/update` checks and `/update --yes` installs.

An update refuses while a run is unfinished, including one waiting for your approval. Finish or cancel
it first. If two different Ru versions are open, one reports `host_version_mismatch`; close the other.

## Uninstall

For the CLI, run `ru uninstall` and confirm in the wizard it opens (close other CLI windows first).
This removes the CLI files and its `PATH` entry. It keeps your sessions, API keys and the shared Ru
profile.

## Next

[Connect a provider and run your first task](first-task.md).
