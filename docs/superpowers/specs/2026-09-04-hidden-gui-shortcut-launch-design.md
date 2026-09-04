# Hidden GUI Shortcut Launch Design

**Date:** 2026-09-04

## Goal

Open Ru from its Windows shortcuts without flashing a Command Prompt or
PowerShell window. Backend, Worker, and Broker startup remain owned by the GUI
and continue to run without visible console windows.

## Root cause

The current Ru shortcuts target `D:\LLM\start_dev.bat`. Windows therefore opens
`cmd.exe` before the batch file starts the published WinUI executable. The
public Host and its Worker/Broker children already use hidden, no-console
process start settings; changing their lifecycle would not remove this launcher
flash.

## Design

Add a small idempotent PowerShell installer under `scripts/` that resolves the
published `RuApp.exe`, verifies that it exists, and creates or updates the
canonical desktop `Ru Desktop App.lnk` and repository `Ru Assistant.lnk` to
target that executable directly. If the legacy desktop
`Run MiniGPT Dev.lnk` exists, update it too, but do not recreate it after the
user removes it. Each shortcut uses `D:\LLM` as its working directory, has no
arguments, and starts in a normal window. The installer accepts explicit root
and shortcut paths for isolated tests, must not launch the GUI, and must not
modify unrelated shortcuts.

Update the existing desktop Ru shortcuts and `D:\LLM\Ru Assistant.lnk` with the
same target. Keep `start_dev.bat` as an explicit diagnostic launcher so startup
checks and visible failure messages remain available to developers.

## Failure behavior

If the published GUI executable is missing, the installer exits nonzero and
leaves existing shortcuts unchanged. Shortcut updates are limited to the exact
known paths; missing canonical shortcuts are created, while the missing legacy
shortcut and unrelated `.lnk` files are ignored.

## Verification

- A deterministic test runs the installer against temporary shortcut paths and
  inspects each resulting `.lnk` target, working directory, and arguments.
- A negative test proves a missing `RuApp.exe` changes no shortcut.
- Inspect the real shortcuts after installation.
- Launch the resulting shortcut and confirm Ru opens while no `cmd.exe` or
  PowerShell launcher process appears; then close Ru and verify its owned
  Backend/Worker/Broker process tree exits.
- Re-run the focused launcher tests and the relevant GUI/client tests.

## Documentation

Current operator documentation that claims the desktop shortcut targets
`start_dev.bat` must be updated. Historical plans remain unchanged unless they
present that claim as current behavior.
