<div align="center">

<img src="docs/assets/wordmark.svg" width="300" alt="ru agent" />

**A coding agent for Windows that runs on your machine and answers to you.**

No Ru account, no telemetry. API keys stay in Windows Credential Manager;
prompts, attachments and tool results go to the model provider you select.

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](#requirements)
[![WinUI 3](https://img.shields.io/badge/UI-WinUI%203-0078D4?logo=windows&logoColor=white)](#requirements)
[![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078D4?logo=windows&logoColor=white)](#requirements)
[![License: MIT](https://img.shields.io/badge/license-MIT-3DA639)](LICENSE)

</div>

---

## At a glance

| | |
| --- | --- |
| **Platform** | Windows 10/11 x64 — self-contained folder, nothing to install |
| **Surfaces** | WinUI 3 desktop app · full-screen terminal UI · stdio RPC bridge + TypeScript SDK |
| **Model access** | Your provider, your key; no Ru account and no proxy in the middle |
| **Permissions** | Three session-scoped presets + explicit workspace trust + single-use approvals |

## What Ru does for you

You open a chat, describe what you want, and Ru works on your project:

- **Reads and searches your files** before changing them.
- **Edits and writes files** — the diff lands only after you can see it.
- **Runs your build, tests and git commands** in a terminal you can watch, with
  output streamed into the conversation.
- **Runs long work in the background** — long commands move to a background
  task instead of blocking the conversation.
- **Searches the web** when you ask, with results sanitized and cited.
- **Keeps a task list** for multi-step work.
- **Loads workspace skills** — a `SKILL.md` in your repository teaches Ru a
  workflow; it stays descriptive guidance and never grants extra powers.

## How the pieces fit

| Component | Path | Role |
| --- | --- | --- |
| **RuApp** | `gui-winui3/` | WinUI 3 desktop app — chat, approvals, terminal panel, settings |
| **RuBackend** | `backend-dotnet/RuBackend/` | ASP.NET Core Public Host — the local HTTP surface, SSE events, run orchestration |
| **RuAgent.Broker** | `backend-dotnet/RuAgent.Broker/` | The execution authority library — every tool call runs through it under policy and approval |
| **RuAgent.Cli** | `backend-dotnet/RuAgent.Cli/` | Terminal tracer: one-shot runs, full-screen TUI, session tooling |

The three executables ship side by side in one folder; the app starts the Host
itself, which executes tools in-process, and the CLI drives the same loopback
surface. Business data lives in one local SQLite database, `agent_harness.db`;
API keys stay in Windows Credential Manager. See
[the persistence decision](docs/adr/0013-consolidate-business-persistence-in-sqlite.md).

## Autonomy, trust, and approval

Every run uses one of three canonical, session-scoped presets:

- **`plan`**: workspace reads and Web run automatically; terminal and mutation
  requests are denied outright (read-only, no per-change asks).
- **`standard`**: workspace mutation and executable/PowerShell launches in a
  workspace working directory run automatically. External structured paths and
  external launch directories ask. Terminal children run as the Windows user
  without OS filesystem or network confinement.
- **`full_access`**: the skip-all mode — external structured files and arbitrary
  terminal commands run automatically; structured protected paths (the harness's
  own key/database files and runtime descriptors) and hard-invalid requests are
  still denied.

Workspace trust is separate from the preset and must be approved **explicitly
once per canonical identity**: the GUI asks in a dialog, CLI `run` and `chat`
require `--trust-workspace`, and moving or recreating the workspace requires
trust again. `full_access` never implies trust, and carries its own explicit
gate — `--confirm-full-access` on the CLI, a confirmation dialog in the GUI.
Both gates are guardrails against accidental escalation, not a boundary against
code already running as your user; every preset change is recorded in the
activity log.

> **Full Access terminal warning:** commands run as the current Windows user,
> can read or change data outside the workspace, and can use the network. There
> is no OS filesystem or network sandbox or confinement; process timeout, output
> cap, cancellation and process-tree cleanup still apply.

Approvals are single-use and bound to the exact call. `allow_always` remembers a
bounded pattern as a durable project rule — a terminal command prefix (resolved
executable plus at most two leading non-flag argv tokens, in the exact launch
directory), or an approved external folder covering its subtree. `rule_asks` and
`terminal_protected_path_requires_approval` never become reusable; current
reasons include `external_directory_required`,
`terminal_external_cwd_requires_approval` and
`terminal_timeout_requires_approval`, while `web_approval_required` remains a
compatibility reason for an explicit ask rule. No provider, model, protocol or
tool fallback bypasses a denial.

### Background tasks

`bash` with `background: true` returns immediately with an execution id, and a
foreground command that outlives its approved timeout moves to the background
instead of being killed. Tasks are session-scoped and bounded (30-minute
default): page output with `read_output`, stop one with `kill`, or use the GUI's
**Tasks** flyout / `/tasks` in the CLI.

## Your data stays local

- **Everything runs on your machine.** The app's surface is bound to
  `127.0.0.1` behind a bearer token — nothing listens on the network.
- **Keys** live in Windows Credential Manager (DPAPI), never in the app.
- **Conversation payloads** are AES-GCM 256 encrypted under a key only your user
  account can read, and it fails closed — lose the key and the payloads are gone
  rather than silently re-keyed.
- **Data** lives in `%LOCALAPPDATA%\Ru`. Delete it and Ru forgets everything.

## Requirements

**Windows 10 or 11, 64-bit** (22H2 / 24H2 recommended). The published build
installs nothing — the .NET runtime and the Windows App Runtime ship inside the
folder. Building from source needs the **.NET SDK 10.0 (x64)**, and VS 2022 C++
Build Tools only when building native components.

## Getting started

```powershell
git clone https://github.com/OniAntou/Ru-Agent.git
cd Ru-Agent
.\build.bat          # Release, self-contained, flattened into one directory
.\start_dev.bat      # or launch the published app directly
```

The build ends with a folder you can copy anywhere and run `RuApp.exe` from.
First launch: open **Settings**, paste an API key for your provider, pick a
model, open a project folder, and start chatting.

## Terminal UI

`ru agent chat` drives a full-screen TUI rendered by **Terminal.Gui v2** on any
interactive, colour-capable terminal of at least 80×24; redirected output,
`--json`, `--classic`, `NO_COLOR`, `TERM=dumb` and smaller viewports stay on the
sequential path.

The layout is OpenCode-inspired with Ru's colours: a centred landing wordmark, a
padded transcript, and a square prompt frame that shares the terminal background
with the session preset and the active model on its edges. The prompt grows with
your draft (Ctrl+J, Shift+Enter or a trailing backslash starts a new line) and
keeps pasted code, the cursor and an oversized draft. The footer carries the
shortcuts, queue status and a colour wave while a run is live.

Ctrl+P opens the command palette; PageUp/PageDown and Ctrl+Home/End navigate
output during streaming. `/new`, `/sessions`, `/resume`, `/follow`, `/files` and
`/tasks` control session continuity, project context and background work. See the
[CLI guide](backend-dotnet/RuAgent.Cli/README.md) and the
[capability and acceptance map](docs/operations/coding-agent-capabilities.md) for
limits and remaining gaps.

## Build and test

```powershell
.\scripts\test_dotnet.ps1 -Configuration Debug   # 12 projects, one matrix at a time
.venv\Scripts\python.exe -m pytest tests -q      # documentation and route guards
```

`ci.yml` runs the 12 test projects, the route contract and the repository guards.
The suite takes a machine-wide lock and reports contention as a failure (exit
code 75) rather than a flaky result.

## Learning more

| Document | What is in it |
| --- | --- |
| [docs/README.md](docs/README.md) | The documentation index |
| [docs/architecture.md](docs/architecture.md) | How the three executables fit together |
| [CONTEXT.md](CONTEXT.md) | The domain language |
| [docs/operations/agent-tools.md](docs/operations/agent-tools.md) | Every tool, its bounds, and the policy matrix |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Setup, verification by impact, and contribution practices |
| [SECURITY.md](SECURITY.md) | How to report a vulnerability privately |
| [AGENTS.md](AGENTS.md) | Agent workflow, execution boundaries, and implementation guidance |

## License

Ru is released under the [MIT License](LICENSE): use it, modify it, share it and
build on it — including commercially — with no strings beyond keeping the
copyright notice.
