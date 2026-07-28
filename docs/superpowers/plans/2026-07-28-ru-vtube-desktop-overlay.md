# Ru VTube Studio Desktop Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Display Ru's VTube Studio model as a transparent, always-on-top, click-through Windows desktop overlay using VTube Studio Spout2 and Sprout.

**Architecture:** VTube Studio remains the single model renderer and continues receiving Ru's API-driven lipsync, actions, and sway. It exports a transparent GPU texture with Spout2; Sprout receives the `VTubeStudioSpout` sender and renders it as a local desktop overlay. No Ru source file, virtual camera, OBS scene, Windows service, or driver is added.

**Tech Stack:** Windows 10/11, VTube Studio for Windows, Spout2, Sprout single-file executable.

---

### Task 1: Verify the local prerequisites

**Files:**
- Read: `D:\Steam\steamapps\common\VTube Studio\VTube Studio.exe`
- Read: `D:\LLM\src\mini_gpt\vts_manager.py`

- [ ] **Step 1: Confirm VTube Studio and Ru are available**

Run:

```powershell
Get-Item 'D:\Steam\steamapps\common\VTube Studio\VTube Studio.exe'
Get-Process python -ErrorAction SilentlyContinue | Select-Object Id, StartTime
```

Expected: the VTube Studio executable exists and the Ru GUI can be running
independently of the overlay.

- [ ] **Step 2: Confirm the existing VTube integration remains display-only for Sprout**

Inspect `src\mini_gpt\vts_manager.py` and verify that `VoiceVolume`, the VTS
action catalog, and `LLM_Sway` still communicate directly with VTube Studio.
Do not modify those values for this overlay setup.

### Task 2: Acquire and start the local overlay receiver

**Files:**
- Create: `D:\LLM\tools\sprout\Sprout.exe` (downloaded third-party executable)

- [ ] **Step 1: Open the official free-download page**

Run:

```powershell
Start-Process 'https://periskulum.gumroad.com/l/sprout'
```

Expected: the official Sprout Gumroad page opens. Complete its free download
in the browser and save the single executable as
`D:\LLM\tools\sprout\Sprout.exe`. Do not accept an installer, driver, or
unrelated bundled download.

- [ ] **Step 2: Verify the downloaded executable before starting it**

Run:

```powershell
Get-Item 'D:\LLM\tools\sprout\Sprout.exe' | Select-Object FullName, Length, LastWriteTime
Get-AuthenticodeSignature 'D:\LLM\tools\sprout\Sprout.exe' | Select-Object Status, StatusMessage
```

Expected: exactly one executable exists at the expected path. Record the
signature result; a missing signature is reported to the user before launch,
not treated as verified publisher identity.

- [ ] **Step 3: Launch Sprout without installation**

Run:

```powershell
Start-Process -FilePath 'D:\LLM\tools\sprout\Sprout.exe' -WorkingDirectory 'D:\LLM\tools\sprout'
```

Expected: a Sprout icon appears in the system tray and no Windows installer or
administrator prompt is required.

### Task 3: Publish VTube Studio's transparent Spout2 source

**Files:**
- Modify through VTube Studio UI only: current model scene background and Spout2 output settings

- [ ] **Step 1: Select the capture-transparent background**

In VTube Studio, choose the **Color Picker Background**, then enable
**Transparent in capture**. Keep the model itself visible in VTube Studio; do
not change `VoiceVolume`, Live2D parameter mappings, or model hotkeys.

- [ ] **Step 2: Start Spout2 output**

In VTube Studio, open **Settings → Spout2** and select **Start**. The expected
sender name is `VTubeStudioSpout`; if VTube Studio displays a different name,
copy that exact case-sensitive name for Sprout.

- [ ] **Step 3: Confirm the sender exists**

Open Sprout from the system tray, then **Settings → General**. Enter the
sender name exactly. An avatar without a rectangular background is the passing
visual check; a blank overlay means return to VTube Studio and ensure Spout2 is
still started.

### Task 4: Configure the desktop overlay and verify recovery

**Files:**
- Modify through Sprout UI only: `%AppData%\Sprout\` settings created by Sprout

- [ ] **Step 1: Position and render the overlay**

In Sprout **Settings → General**, set **Monitor** to `0` first (primary
display), leave **Fullscreen** disabled, and set **Over Taskbar** off. In
**Display**, use 60 FPS for smooth motion; use 30 FPS if GPU use is higher than
desired. Use crop controls to place the avatar in a screen corner.

- [ ] **Step 2: Enable click-through only after positioning**

Enable **Click-Through** from the tray icon or **Settings → General**. Confirm
that clicking through the avatar reaches the desktop application beneath it.
To reposition later, disable Click-Through from the tray icon first.

- [ ] **Step 3: Verify Ru-driven motion and stop behavior**

Send one voice-enabled Ru reply and one avatar-action request. Confirm lip-sync,
body sway, and any selected hotkey motion appear in the overlay. Then exit
Sprout from its tray menu and confirm the overlay disappears while VTube Studio
and Ru continue running normally.

- [ ] **Step 4: Record the operating note**

Add the verified sender name, selected monitor, and preferred FPS to
`README.md` under **VTube Studio actions**. Do not save a token, license key,
or absolute user-profile path in the repository.
