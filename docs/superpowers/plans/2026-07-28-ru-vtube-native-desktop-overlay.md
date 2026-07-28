# Ru Native VTube Studio Desktop Overlay Implementation Plan (superseded)

> **Outcome (2026-07-28):** Tasks 1 and 2 were implemented and their Pester
> checks passed. Task 3 live validation disproved the design: VTube Studio
> retained an opaque `#00FF00` background after its color-key state was
> successfully applied and read back, while a control window using the same
> Win32 calls became transparent. The VTube Studio window was restored with
> `-Disable`; this implementation must not be used. A future solution needs an
> overlay receiver for VTS's transparent Spout2, virtual-webcam, or NDI output.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a reversible PowerShell command that turns the active VTube Studio window into a chroma-keyed, always-on-top, optionally click-through desktop overlay.

**Architecture:** The script uses inline C# P/Invoke declarations for `GetWindowLongPtr`, `SetWindowLongPtr`, `SetLayeredWindowAttributes`, and `SetWindowPos`. On enable it records the VTube Studio window identity and original styles in `%LOCALAPPDATA%\Ru\vtube-overlay.json`; on disable it restores only that same still-running process window.

**Tech Stack:** PowerShell 5.1, .NET/Win32 User32 APIs, Pester 3.4, VTube Studio for Windows.

---

### Task 1: Add a testable window-style helper

**Files:**
- Create: `scripts/set_vtube_desktop_overlay.ps1`
- Create: `tests/test_vtube_desktop_overlay.ps1`

- [ ] **Step 1: Write the failing helper tests**

Use Pester to dot-source the script with `-LibraryOnly`, then assert that:

```powershell
Describe 'VTube desktop overlay helpers' {
    It 'parses an RGB color key into a Win32 COLORREF value' {
        ConvertTo-ColorRef '00FF00' | Should Be 65280
    }

    It 'rejects an invalid color key' {
        { ConvertTo-ColorRef 'green' } | Should Throw
    }

    It 'builds a state path under LocalAppData' {
        Get-OverlayStatePath | Should Match 'Ru\\vtube-overlay.json$'
    }
}
```

- [ ] **Step 2: Run the helper tests and confirm they fail**

Run:

```powershell
Invoke-Pester .\tests\test_vtube_desktop_overlay.ps1
```

Expected: FAIL because the script and helper functions do not yet exist.

- [ ] **Step 3: Implement the helpers and Win32 declarations**

Implement `ConvertTo-ColorRef`, `Get-OverlayStatePath`, `Get-VTubeWindow`,
and a C# `RuOverlay.NativeMethods` type with pointer-safe window-style calls.
Require exactly one process named `VTube Studio` with a non-zero main window
handle; otherwise throw without changing any window.

- [ ] **Step 4: Run the helper tests and confirm they pass**

Run:

```powershell
Invoke-Pester .\tests\test_vtube_desktop_overlay.ps1
```

Expected: all helper tests PASS.

### Task 2: Implement reversible overlay commands

**Files:**
- Modify: `scripts/set_vtube_desktop_overlay.ps1`
- Extend: `tests/test_vtube_desktop_overlay.ps1`

- [ ] **Step 1: Write the failing command-contract tests**

Add tests that invoke the script with `-LibraryOnly` and mock its native calls.
Assert that enable adds `WS_EX_LAYERED`, adds `WS_EX_TRANSPARENT` only with
`-ClickThrough`, saves `processId`, `windowHandle`, `style`, and `exStyle`,
and requests `HWND_TOPMOST` plus `SWP_NOACTIVATE`. Assert that disable rejects
a state file whose process ID or window handle no longer matches the active
VTube Studio window.

- [ ] **Step 2: Run the command-contract tests and confirm they fail**

Run:

```powershell
Invoke-Pester .\tests\test_vtube_desktop_overlay.ps1
```

Expected: FAIL because enable/disable dispatch has not been implemented.

- [ ] **Step 3: Implement enable and disable**

Expose mutually exclusive `-Enable` and `-Disable` switches, an optional
`-ClickThrough`, and `-ColorKey` defaulting to `00FF00`. Enable writes the
state atomically after all Win32 calls succeed; disable restores the saved
styles only after verifying the saved VTube Studio process ID and handle. Both
commands report the PID, handle, color key, and current styles. Any failed
native call throws its Win32 error and does not delete the recovery state.

- [ ] **Step 4: Run the command-contract tests and confirm they pass**

Run:

```powershell
Invoke-Pester .\tests\test_vtube_desktop_overlay.ps1
```

Expected: all helper and command-contract tests PASS.

### Task 3: Apply the overlay and verify recovery on the live VTube window

**Files:**
- Modify through VTube Studio UI only: Color Picker Background set to `#00FF00`
- Modify: `README.md`

- [ ] **Step 1: Prepare VTube Studio's chroma background**

In VTube Studio select **Color Picker Background** and set it to pure green
`#00FF00`. Keep the model window open and unminimized. Do not modify model
parameters, `VoiceVolume`, or `LLM_Sway` mappings.

- [ ] **Step 2: Enable visual overlay without click-through**

Run:

```powershell
.\scripts\set_vtube_desktop_overlay.ps1 -Enable -ColorKey 00FF00
```

Expected: VTube Studio remains visible over ordinary windows and the green
background is transparent. Confirm the avatar has no green-colored regions
that disappear.

- [ ] **Step 3: Enable click-through only after visual approval**

Run:

```powershell
.\scripts\set_vtube_desktop_overlay.ps1 -Enable -ClickThrough -ColorKey 00FF00
```

Expected: mouse input passes to the window below the avatar. The script's
`-Disable` command remains available from a normal PowerShell terminal.

- [ ] **Step 4: Verify Ru animation and recovery**

Open Ru GUI, trigger a voice-enabled reply and an avatar hotkey request, then
visually confirm lip-sync, sway, and action movement in the overlay. Run:

```powershell
.\scripts\set_vtube_desktop_overlay.ps1 -Disable
```

Expected: the VTube Studio window becomes ordinary, non-topmost,
mouse-interactive again without closing VTube Studio or Ru.

- [ ] **Step 5: Document the verified commands**

Add the enable, click-through, and disable commands to the `README.md` VTube
Studio section, including the green color-key warning and the requirement that
the VTube Studio window remains open and unminimized.

- [ ] **Step 6: Commit the implementation**

```powershell
git add scripts/set_vtube_desktop_overlay.ps1 tests/test_vtube_desktop_overlay.ps1 README.md
git commit -m "feat: add VTube Studio desktop overlay"
```
