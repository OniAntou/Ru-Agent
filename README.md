<div align="center">

<img src="docs/assets/wordmark-clean.svg" width="394" alt="ru agent" />

[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4?style=flat-square)](#quick-start) [![C# / .NET](https://img.shields.io/badge/C%23-.NET%2010-512BD4?style=flat-square)](CONTRIBUTING.md) [![CI](https://github.com/OniAntou/Ru-Agent/actions/workflows/ci.yml/badge.svg?branch=dev)](https://github.com/OniAntou/Ru-Agent/actions/workflows/ci.yml) [![MIT](https://img.shields.io/badge/License-MIT-00a85a?style=flat-square)](LICENSE)

[**Get started**](#quick-start) · [**Terminal guide**](backend-dotnet/RuAgent.Cli/README.md) · [**Discuss**](https://github.com/OniAntou/Ru-Agent/discussions) · [**Contribute**](CONTRIBUTING.md)

</div>

# Ru Agent

**A coding agent built for Windows. Choose your model provider, bring your own
API key, and work from a native desktop app or your terminal.**

Describe a change. Ru reads your project, edits files, runs commands and keeps
the work visible in the conversation. Your project and conversation storage
stay on your machine; prompts and tool results go to the provider you select.

> **Early development.** A Windows x64 Preview is available; source builds are also supported.
> Expect rough edges; reproducible bug reports help shape the next version.

## Why Ru?

| | What you get |
| --- | --- |
| **A native Windows app** | A WinUI 3 chat surface with an activity feed, file changes and an integrated terminal. |
| **A terminal interface** | A full-screen TUI with session history, model selection, slash commands and a multiline composer. |
| **Your provider and keys** | Choose the model/provider in Settings. No Ru account; API keys use Windows Credential Manager. |
| **Work you can follow** | Read/search files, make edits, run builds/tests, inspect tool activity and track background commands. |
| **Reusable workflows** | Add repository instructions and `SKILL.md` workflows for tasks you repeat. |

Ru's orchestration runs locally. It also supports web search/fetch, which make
network requests when used. Local-first does **not** mean offline inference or
a bundled local model.

## Install and update

Separate Windows x64 Desktop and CLI installers are available in
[0.1.1-preview.1](https://github.com/OniAntou/Ru-Agent/releases/tag/v0.1.1-preview.1). Each includes its
matching backend; the .NET SDK is only needed when building from source. Desktop
updates from **Settings → About → Updates**; CLI uses `ru update` and TUI uses
`/update`. Stable is the default, with Preview available by choice.

See [installation, channels and recovery](docs/operations/distribution-and-updates.md)
for package names, CLI bootstrap instructions and release verification limits.
Install CLI from Command Prompt:

```cmd
powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/OniAntou/Ru-Agent/dev/scripts/install.ps1'))) -Channel Preview"
```

Open a new terminal and run `ru`. The script downloads, verifies and installs the
CLI package without a setup wizard. Preview is opt-in; no Stable release is
published yet. The initial packages are unsigned.

## Quick start

### 1. Build on Windows

Install the **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**
and Git, then run in PowerShell:

```powershell
git clone https://github.com/OniAntou/Ru-Agent.git
cd Ru-Agent
.\build.bat
.\start_dev.bat
```

The build creates a self-contained folder with `RuApp.exe`, `RuBackend.exe`
and `RuAgent.Cli.exe`. The published app includes .NET and the Windows App
Runtime. See [the contributor guide](CONTRIBUTING.md#development-environment)
for the complete build requirements and output path.

### 2. Choose a model and open a project

In the desktop app, open **Settings**, configure your provider/API key and pick
a model. Open your project folder, review the workspace trust prompt, then ask
Ru to do a small task:

> Find where this project handles configuration. Explain it before changing anything.

Or start a terminal chat from the repository:

```powershell
.\bin\ru.cmd agent chat
```

See the [TUI guide](backend-dotnet/RuAgent.Cli/README.md) for commands, keyboard
shortcuts and session navigation.

## You choose the permissions

| Preset | Intended use |
| --- | --- |
| `plan` | Explore and plan with read-only tools. |
| `standard` | Work with the workspace under the standard policy; requests beyond it can require approval. |
| `full_access` | Allow broader tool access; structured tools still deny protected paths. |

Workspace trust and explicit approvals are part of the workflow. Instructions
and skills guide the agent; they do not grant execution authority.

**Terminal commands run as your Windows user, without an OS filesystem or
network sandbox.** Standard launch permission does not confine a child process
to the workspace. Read the [tool policy](docs/operations/agent-tools.md) for
exact boundaries.

## Help build Ru

Use Ru on a small project and tell us what worked or broke. A short reproduction
helps us improve the next version.

- [Report a bug](https://github.com/OniAntou/Ru-Agent/issues/new?template=bug_report.yml): include the surface, commit/version and reproduction steps.
- [Suggest a feature](https://github.com/OniAntou/Ru-Agent/issues/new?template=feature_request.yml): describe the workflow you want to improve.
- [Start a discussion](https://github.com/OniAntou/Ru-Agent/discussions): share a use case, ask a question or compare workflows.
- [Contribute code or docs](CONTRIBUTING.md): keep changes scoped and verify the behavior you touched.

If Ru is useful to you, a star helps other developers discover the project.
Security reports belong in [private advisories](SECURITY.md), not public issues.

## Documentation

| Start here | Go deeper |
| --- | --- |
| [Documentation index](docs/README.md) | [Architecture](docs/architecture.md) |
| [Terminal UI guide](backend-dotnet/RuAgent.Cli/README.md) | [Domain language](CONTEXT.md) |
| [Tool policy](docs/operations/agent-tools.md) | [Verification evidence](docs/operations/verification.md) |
| [Contributing](CONTRIBUTING.md) | [Security policy](SECURITY.md) |

## License

[MIT](LICENSE). Use, modify and distribute Ru, including commercially, while
keeping the license notice.
