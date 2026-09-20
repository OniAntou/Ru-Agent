<div align="center">

# Ru Agent

**A coding agent that is a Windows program, not a runtime that hosts one.**

No Python. No Node. No VM. No sidecar. Four native processes, one directory,
nothing to install.

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](#requirements)
[![WinUI 3](https://img.shields.io/badge/UI-WinUI%203-0078D4?logo=windows&logoColor=white)](#what-runs)
[![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078D4?logo=windows&logoColor=white)](#requirements)
[![Tests](https://img.shields.io/badge/tests-3%2C589%20passing-3FB950)](#testing)
[![Tools](https://img.shields.io/badge/tools-10-8B5CF6)](#tools)
[![Routes](https://img.shields.io/badge/routes-40%20HTTP-6E7681)](#testing)

</div>

---

## Why native

Every layer that is not native has to be started, kept warm, marshalled across, and
shut down. An interpreter runtime has to load, a sidecar has to be supervised, a
foreign VM has to be bridged. Each costs memory while idle and latency while
working, and each is a thing that can be slow or absent on the user's machine.

So this project has none of them.

| | |
| :--- | :--- |
| 🧩 **One runtime** | Pure .NET 10. No Python, Node, JVM, or Electron anywhere in the product. |
| 📦 **One directory** | `build.bat` flattens five executables side by side. Unpack it and run. |
| ✈️ **Nothing to install** | The .NET runtime **and** the Windows App Runtime ship inside the folder. |
| 🪟 **Windows subsystems** | WinUI 3, Job Objects, DPAPI, ConPTY, the K32 process API — called directly. |
| 🧹 **Nothing left behind** | One Job Object with `KillOnJobClose` owns the child tree, so runs end clean. |
| ☁️ **No model in memory** | Generation is a cloud call. No weights, no tokenizer, no GPU allocation. |

> **What "native" means here.** Every process is a native Windows executable calling
> Windows directly — but the build is **not** ahead-of-time compiled to machine code.
> Release publishes are precompiled with ReadyToRun; Native AOT is not enabled. See
> [Not done yet](#not-done-yet).

## What runs

```text
RuApp.exe (WinUI 3)                 RuAgent.Cli.exe (TUI)
        │                                     │
        └─────── start-or-attach ─────────────┘
                 via RuAgent.Client
                          │
                          ▼
RuBackend.exe --managed-runtime            127.0.0.1:8000
  ASP.NET Core Minimal APIs · 40 routes · loopback bearer
  sessions · history · durable harness · SQLite WAL
  owns one Windows Job Object (kill-on-close) for the whole child tree
        │                                     │
        ├──────────────────┬──────────────────┘
        ▼                  ▼
RuAgent.Runtime.Worker            RuAgent.Broker
  127.0.0.1:8005                    dynamic loopback port
  Worker v2: one step =             11 tools · policy · approvals
  one generate_reply                files · terminal · web
  chat_completions ·                path canonicalization · containment
  openai_responses ·                one Job Object per terminal call
  anthropic_messages
```

Four processes at steady state. The Worker does not spawn per session and the Broker
does not spawn per request, so a second chat does not add a process. `RuAgent.Client`
is the only entry point: it reads the loopback descriptor or starts the Host.

## Tools

**11 tools**, declared once in `RuAgent.Protocol` (`AgentToolDefinitionCatalog`). The
prompt lists each tool thinly (id, version, one-line purpose); the full schema
and digest travel through the Worker's `model_tools` and stay digest-bound in
the prompt receipt, and the Broker **refuses to start** if a manifest disagrees
with the digest. Every schema is strict (`additionalProperties: false`).

| Tool | Arguments | Purpose |
| :--- | :--- | :--- |
| `read` | `path`, `offset`, `limit` | Read one file, paged by line. |
| `list` | `path`, `max_entries` (1–512) | List one directory with metadata. |
| `grep` | `query`, `path`, `file_glob`, `case_sensitive`, `is_regex`, `max_results` (1–64) | Search file contents. |
| `glob` | `pattern`, `path`, `max_results` (1–64) | Find files by pattern. |
| `help` | `query` (1–256), `max_results` (1–5) | Search five bundled offline documents. |
| `todo_write` | `todos` (max 64 rows of `content` + `status`) | Replace the session task list shown in the transcript. |
| `write` | `op=write\|delete\|restore`, … | Write, delete with a recovery copy, or restore. |
| `edit` | `path`, `old_string`, `new_string` | Replace an exact string. |
| `bash` | `mode=run\|shell`, … | Run an executable or script with `timeout_ms`. |
| `web_search` | `query` (1–512), `max_results` (1–10) | Search the web; approval each time. |
| `web_fetch` | `url` (≤2048) | Fetch one page; approval each time. |

`read`, `list`, `help`, `todo_write`, `grep` and `glob` never prompt. `write`, `edit`
and `bash` do.
`web_search` and `web_fetch` always do.

Every call is bounded — `read` caps at 256 KiB, `grep` at 64 results and 1 MiB per
file, `bash` output is head/tail truncated, and terminal calls die with their own Job
Object on timeout. A pathological input degrades into a bounded result, never a
memory spike.

## Requirements

- **Windows 10 or 11, 64-bit** (22H2 / 24H2 recommended)
- **.NET SDK 10.0** (x64) — to build; the published output is self-contained
- **Python 3.11–3.14** — repository tooling only, never part of the product
- **VS 2022 C++ Build Tools** — only if building native components from source

## Build and run

```powershell
.\build.bat          # Release, self-contained, flattened into one directory
.\start_dev.bat      # development launch
```

`build.bat` runs 13 stages and ends with a directory you can copy anywhere:

```powershell
# After the build, launch the distributable directly:
gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe
```

The GUI and CLI **start-or-attach**: if a Host is already running they attach to it,
otherwise they start one, and that Host owns the Worker and Broker in a single Job
Object.

## Testing

```powershell
.\scripts\test_dotnet.ps1 -Configuration Debug   # 13 projects, 3,545 tests
.venv\Scripts\python.exe -m pytest tests -q      # documentation and route guards
```

Run one matrix at a time. The suite starts real processes and drives real HTTP hosts, so a
concurrent run — another matrix, or anything that publishes and starts the app — makes
timing-sensitive tests fail for reasons that have nothing to do with the code. The script
takes a machine-wide lock and refuses to start while one is held (exit code 75), rather than
running anyway and reporting contention as a defect. That is the whole guard: a failure is
reported as a failure, with no retry to blur it, because a retry would hide a genuinely
flaky test instead of fixing it.

`public-route-contract.json` is the source of truth for the **40 HTTP routes** (19
non-Agent plus 21 under `/agent/v1/*`). A test compares the live `EndpointDataSource`
against that file, so adding or removing a route without updating it fails CI.

Two guards protect the interface, not the wording: no tool manifest may drift from its
Protocol digest, and the permission vocabulary below must stay accurate.

## Security and data

- **Data** lives in `%LOCALAPPDATA%\Ru`. `RU_DATA_ROOT` and `RU_LOG_ROOT` isolate it
  completely for tests and CI.
- **API keys** go through Windows Credential Manager (DPAPI) on the Host side. The GUI
  holds no credential: a key is typed into a `PasswordBox` and posted to `/settings`.
- **Payloads** in `agent_harness.db` are AES-GCM 256 encrypted under a DPAPI-protected
  key (`harness_key.dat`). It fails closed — lose the key and the payloads are gone.
- **Permissions are session-scoped.** Every run starts at the `standard` preset;
  `trusted` is opt-in per session with interactive confirmation. An outside-folder
  approval opens a grant for that session only, terminal tokens are single-use, and
  **no grant survives a restart**.
- **Approval** is required for outside-workspace access
  (`external_directory_required`), deletion (`terminal_delete_requires_approval`),
  unknown executables (`terminal_unknown_executable`), outside-workspace mutation
  (`terminal_external_mutation_requires_approval`), extended timeouts
  (`terminal_timeout_requires_approval`) and web access (`web_approval_required`).
- **Workspace trust** binds to volume serial and root file identity, not to a marker
  file inside the repository.

## Layout

<details>
<summary><b>Click to expand the tree</b></summary>

```text
.
├── backend-dotnet/                    # .NET 10 core
│   ├── RuBackend/                     # ASP.NET Core Public Host (:8000)
│   ├── RuAgent.Broker/                # Tool execution, policy, approvals
│   ├── RuAgent.Runtime.Worker/        # Worker v2 (:8005), provider adapters
│   ├── RuAgent.Runtime.Adapters/      # Three provider dialects
│   ├── RuAgent.Harness.Core/          # State machines, prompt, tool pipeline
│   ├── RuAgent.Harness.Persistence/   # SQLite schema v7, AES-GCM, outbox
│   ├── RuAgent.Protocol/              # 11 tool definitions, envelopes, contracts
│   ├── RuAgent.SessionInterchange/    # Other agents' session files, both directions
│   ├── RuAgent.Hosting/               # Layout, Job Object, descriptor
│   ├── RuAgent.Client/                # Shared client library
│   ├── RuAgent.Cli/                   # Terminal UI
│   ├── RuAgent.Connectors/            # Calendar/task/mail (not tools)
│   └── *.Tests/                       # 12 test projects
├── gui-winui3/                        # WinUI 3 desktop app
│   ├── Views/                         # ChatView, SettingsView, Diagnostics, LogView
│   ├── Services/                      # ApiService, controllers, presentation
│   └── Controls/                      # MarkdownBlock, TerminalPanel, ApprovalPanel
├── gui-winui3.Tests/                  # Unit and source-contract tests
├── gui-winui3.Harness/                # GUI acceptance through UI Automation
├── tests/                             # Python guards (docs, routes, build, cleanup)
├── scripts/                           # test_dotnet.ps1, launchers, route checks
├── docs/                              # Architecture, operations, ADRs
├── build.bat                          # Release build, 13 stages
└── start_dev.bat                      # Development launch
```

</details>

## Not done yet

Stated plainly so nothing above reads as a stronger claim than it is:

- **Not ahead-of-time compiled.** `PublishReadyToRun` is on for Release and off for
  Debug; Native AOT is not enabled anywhere. Assemblies ship as IL with a ReadyToRun
  native image, still JIT-compiled for paths the image does not cover.
- **No performance gate in CI.** `ci.yml` runs the 12 test projects, the route
  contract and the repository guards. Nothing measures or limits CPU, memory or GPU.
- **No recorded resource baseline.** `scripts/measure_resources.ps1` exists and is
  verified, but no measurement of a published build has been captured into the repo.
- **No installer.** The build produces a runnable folder, not an MSIX or an installer.
- Out of scope for now: subagent orchestration, LSP integration, MCP discovery, and an
  OS-level filesystem or network sandbox.
