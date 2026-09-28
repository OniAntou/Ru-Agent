<div align="center">

# Ru

**A coding agent for Windows that runs on your machine and answers to you.**

No install. No account. No telemetry. Your API keys stay in Windows Credential
Manager and your project never leaves your disk.

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](#requirements)
[![WinUI 3](https://img.shields.io/badge/UI-WinUI%203-0078D4?logo=windows&logoColor=white)](#requirements)
[![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078D4?logo=windows&logoColor=white)](#requirements)
[![Tests](https://img.shields.io/badge/tests-4%2C676%20passing-3FB950)](#build-and-test)
[![Tools](https://img.shields.io/badge/tools-12-8B5CF6)](#what-ru-can-do)
[![Routes](https://img.shields.io/badge/routes-42%20HTTP-6E7681)](#your-data-stays-local)

</div>

---

## What Ru does for you

You open a chat, describe what you want, and Ru works on your project:

- **Reads and searches your files** to understand the codebase before changing it.
- **Edits and writes files** — you see the diff before anything lands.
- **Runs your build, tests, and git commands** in a terminal you can watch, with
  output streamed into the conversation.
- **Searches the web** when you ask, with results sanitized and cited.
- **Keeps a task list** for multi-step work, so progress is visible at a glance.
- **Loads workspace skills** — a `SKILL.md` in your repository teaches Ru a
  workflow; it stays descriptive guidance and never grants extra powers.

Behind that is **12 tools**, each deliberately small and bounded — reading a
file is capped, search results are capped, terminal output is truncated, and
every command dies with its own timeout. A pathological request degrades into
a bounded result, never a frozen app.

## Autonomy, trust, and approval

Every run uses one of three canonical, session-scoped presets:

- **`plan`**: workspace reads and Web run automatically; terminal and mutation
  requests are denied.
- **`standard`**: bounded-local in-workspace mutation runs automatically;
  external structured access, path escape, and risky terminal intent ask first.
- **`full_access`**: external structured files and arbitrary terminal commands
  run automatically; structured protected paths and hard-invalid requests are
  still denied.

Workspace trust is separate from the session preset and must be approved
**explicitly once per canonical identity**. The GUI shows a confirmation
dialog; CLI `run` and `chat` require `--trust-workspace`. Moving or recreating
the workspace produces a new canonical identity and requires trust again.
Selecting `full_access` never implies workspace trust.

Full Access also requires an explicit gate. The GUI keeps its confirmation
dialog; CLI `run` and `chat` require `--confirm-full-access` before changing
session state. `--json` is accepted only with that explicit CLI confirmation.

Be precise about what that gate is. The Host enforces it from the
`X-Interactive-Confirmed` request header, so it is a **guardrail against
accidental escalation**, not a security boundary against code already running as
your user: anything that can read `%LOCALAPPDATA%\Ru\runtime_descriptor.json`
can set that header. The actual boundary is the bearer token in an owner-only
descriptor. Every preset change is recorded in the activity log.

Approvals remain exact-intent and single-use. The current reasons include
`external_directory_required`, `terminal_delete_requires_approval`,
`terminal_unknown_executable`,
`terminal_external_mutation_requires_approval`,
`terminal_timeout_requires_approval`, and
`terminal_protected_path_requires_approval` (the last names a protected location
in the approval and is never offered as a reusable `allow_always` rule).
`web_approval_required` remains a
compatibility reason for an explicit ask rule, but the canonical `plan` profile
allows Web and does not synthesize that approval. No provider, model, protocol,
or tool fallback bypasses a denial.

> **Full Access terminal warning:** commands run as the current Windows user,
> can read or change data outside the workspace, and can use the network. This
> is not a network/path sandbox. Process timeout, output cap, cancellation, and
> process-tree cleanup still apply. Structured file tools separately retain
> canonical/symlink/junction/hard-link containment and protected-path denial.

## Your data stays local

- **Everything runs on your machine.** The app's entire surface is
  **42 HTTP routes** (21 non-Agent plus 21 under `/agent/v1/*`) bound to
  `127.0.0.1` behind a bearer token — nothing listens on the network.
- **Keys** go into Windows Credential Manager (DPAPI) on your side of the
  glass. The GUI holds no credential.
- **Conversation payloads** are AES-GCM 256 encrypted under a key only your
  user account can read, and it fails closed — lose the key and the payloads
  are gone rather than silently re-keyed.
- **Data** lives in `%LOCALAPPDATA%\Ru`. Delete it and Ru forgets everything.

## Requirements

- **Windows 10 or 11, 64-bit** (22H2 / 24H2 recommended)

To run the published build you install nothing — the .NET runtime and the
Windows App Runtime ship inside the folder. To build from source you need the
**.NET SDK 10.0 (x64)**, and **VS 2022 C++ Build Tools** only when building
native components.

## Getting started

```powershell
git clone https://github.com/OniAntou/Ru-Agent.git
cd Ru-Agent
.\build.bat          # Release, self-contained, flattened into one directory
.\start_dev.bat      # or launch the published app directly
```

`build.bat` ends with a folder you can copy anywhere — unpack it on another
PC and run `RuApp.exe` from it:

```powershell
gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe
```

First launch: open **Settings**, paste an API key for your provider, pick a
model, open a project folder, and start chatting.

## Build and test

```powershell
.\scripts\test_dotnet.ps1 -Configuration Debug   # 13 projects, 4,676 tests
.venv\Scripts\python.exe -m pytest tests -q      # documentation and route guards
```

Run one matrix at a time — the suite takes a machine-wide lock and reports
contention as a failure (exit code 75) rather than a flaky result.

## Learning more

- [docs/README.md](docs/README.md) — the documentation index
- [CONTEXT.md](CONTEXT.md) — the domain language
- [docs/architecture.md](docs/architecture.md) — how the four processes fit together
- [CONTRIBUTING.md](CONTRIBUTING.md) — building, testing, and the rules
- [AGENTS.md](AGENTS.md) — the same rules for AI coding agents

## Not done yet

Stated plainly so nothing above reads as a stronger claim than it is:

- **Not ahead-of-time compiled.** `PublishReadyToRun` is on for Release and off for
  Debug; Native AOT is not enabled anywhere.
- **No performance gate or resource baseline in CI.** `ci.yml` runs the 13 test projects,
  the route contract and the repository guards; nothing measures CPU, memory or GPU
  of a published build yet (`scripts/measure_resources.ps1` exists and is verified).
- **No installer.** The build produces a runnable folder, not an MSIX or an installer.
- Out of scope for now: subagent orchestration, LSP integration, MCP discovery, and an
  OS-level filesystem or network sandbox.
