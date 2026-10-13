<div align="center">

<img src="docs/assets/wordmark.svg" width="300" alt="ru agent" />

**A coding agent for Windows that runs on your machine and answers to you.**

No Ru account, no telemetry. API keys stay in Windows Credential Manager.

[![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078D4?logo=windows&logoColor=white)](#requirements)
[![License: MIT](https://img.shields.io/badge/license-MIT-3DA639)](LICENSE)

</div>

---

You open a chat, describe what you want, and Ru works on your project:

- **Reads and searches your files** before changing them.
- **Edits and writes files** — the diff lands only after you can see it.
- **Runs your build, tests and git commands** in a terminal you can watch.
- **Keeps working in the background** — long commands move to a task instead of
  blocking the conversation, and a task list keeps multi-step work visible.
- **Searches the web** when you ask, with results sanitized and cited.
- **Learns your workflows** — a `SKILL.md` in your repository teaches Ru steps
  you repeat; it stays guidance and never grants extra powers.

Use it as a **WinUI 3 desktop app** or a **full-screen terminal UI**
(`ru agent chat`) — the same agent either way.

## Staying in control

- **Runs on your machine.** Ru talks to nothing but the model provider you
  choose, and nothing listens on the network.
- **Your key, your account.** Keys live in Windows Credential Manager, payloads
  are encrypted on disk, and `%LOCALAPPDATA%\Ru` is all Ru keeps — delete it and
  Ru forgets everything.
- **Three permission presets**: `plan` (read-only), `standard` (works inside your
  workspace and asks for the rest) and `full_access` (skip-all, though protected
  paths stay denied) — plus an explicit, once-per-workspace trust step and
  single-use approvals for anything beyond the preset.
- **Terminal commands run as you**, with no OS sandbox: approve only what you
  would run yourself.

## Get started

The build produces a self-contained folder — nothing to install afterwards:

```powershell
git clone https://github.com/OniAntou/Ru-Agent.git
cd Ru-Agent
.\build.bat
```

Then run `RuApp.exe` from the published folder. First launch: open **Settings**,
paste an API key for your provider, pick a model, open a project folder, and
start chatting. The same folder carries the CLI if you prefer the terminal.

## Requirements

**Windows 10 or 11, 64-bit.** Everything else ships inside the published folder.

## Learn more

| Document | What is in it |
| --- | --- |
| [docs/README.md](docs/README.md) | The documentation index |
| [backend-dotnet/RuAgent.Cli/README.md](backend-dotnet/RuAgent.Cli/README.md) | The terminal UI guide |
| [docs/operations/agent-tools.md](docs/operations/agent-tools.md) | Every tool, its bounds and the policy matrix |
| [docs/architecture.md](docs/architecture.md) | How the app, the Host and the CLI fit together |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Building from source, tests and contribution practices |
| [SECURITY.md](SECURITY.md) | How to report a vulnerability privately |

## License

Ru is released under the [MIT License](LICENSE): use it, modify it, share it and
build on it — including commercially — with no strings beyond keeping the
copyright notice.
