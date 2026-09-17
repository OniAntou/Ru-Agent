# Ru Agent

A coding agent for Windows, built entirely on the native Windows stack. Its
overriding focus is **performance and speed**, and being native is how that is
pursued: no Python, no Node, no virtual machine, no sidecar runtime between the
user and the machine.

.NET 10 compiled for `win-x64`, shipped self-contained, talking to Windows
subsystems directly — WinUI 3 for the shell, Job Objects for process ownership,
DPAPI for secrets, ConPTY for terminals.

---

## Contents

- [1. Focus](#1-focus)
- [2. What "native" means here](#2-what-native-means-here)
- [3. What runs on the machine](#3-what-runs-on-the-machine)
- [4. How the design serves performance](#4-how-the-design-serves-performance)
- [5. Agent tools](#5-agent-tools)
- [6. Requirements](#6-requirements)
- [7. Build and run](#7-build-and-run)
- [8. Testing](#8-testing)
- [9. Project layout](#9-project-layout)
- [10. Security and data](#10-security-and-data)
- [11. Not done yet](#11-not-done-yet)

---

## 1. Focus

Performance and speed, pursued through native implementation.

The reasoning is direct: every layer that is not native is a layer that has to be
started, kept warm, marshalled across, and shut down. An interpreter runtime has
to load. A sidecar has to be supervised. A foreign VM has to be bridged. Each one
costs memory while idle and latency while working, and each one is a thing that
can be slow or absent on the user's machine.

So this project has none of them. Everything that runs is a Windows process built
from this repository, and every capability it needs is a Windows capability.

| Commitment | How it is honoured |
| :--- | :--- |
| No interpreter runtime | Pure .NET 10. There is no Python, Node, JVM, or Electron process anywhere in the product. |
| No foreign VM or sidecar | Four Windows processes at steady state, all built here. Nothing is spawned to broker work. |
| Self-contained delivery | `build.bat` publishes `win-x64` self-contained executables, so the machine needs no separate framework install to run them. |
| Native Windows subsystems | WinUI 3 shell, Windows Job Objects, DPAPI, ConPTY, the K32 process snapshot API. |
| Native process ownership | One Job Object with `KillOnJobClose` owns the whole child tree, so nothing is left resident when a run ends. |
| No in-process model | Generation is a cloud call. No weights, no tokenizer, and no GPU allocation for inference are held by the agent. |

## 2. What "native" means here

Stated precisely, because the word is easy to overclaim:

- **Yes:** every process is a native Windows executable built from this
  repository. There is no interpreted language and no foreign runtime in the
  product. Windows features — WinUI 3, Job Objects, DPAPI, ConPTY, the ToolHelp
  and K32 process APIs — are called directly rather than through a portability
  layer built to abstract them away.
- **No:** the build is not ahead-of-time compiled to machine code.
  `PublishReadyToRun` is explicitly `False` in the WinUI project for both Debug
  and Release, and Native AOT is not enabled anywhere. The assemblies ship as
  managed IL and are JIT-compiled at first use.

That second point is not a contradiction of the goal, it is known headroom. The
project is native by construction; making it precompiled is a further step that
has not been taken, and section [11](#11-not-done-yet) says so.

## 3. What runs on the machine

```text
RuApp.exe (WinUI 3)                 RuAgent.Cli.exe (TUI)
        │                                     │
        └─────── start-or-attach ─────────────┘
                 via RuAgent.Client
                          │
                          ▼
RuBackend.exe --managed-runtime            127.0.0.1:8000
  ASP.NET Core Minimal APIs · 37 routes · loopback bearer
  sessions · history · durable harness · SQLite WAL
  owns one Windows Job Object (kill-on-close) for the whole child tree
        │                                     │
        ├──────────────────┬──────────────────┘
        ▼                  ▼
RuAgent.Runtime.Worker            RuAgent.Broker
  127.0.0.1:8005                    dynamic loopback port
  Worker v2: one step =             10 tools · policy · approvals
  one generate_reply                files · terminal · web
  chat_completions ·                path canonicalization · containment
  openai_responses ·                one Job Object per terminal call
  anthropic_messages
```

Four processes at steady state: the UI, the Host, exactly one Worker, exactly one
Broker. The Worker does not spawn per session and the Broker does not spawn per
request, so opening a second chat does not add a process or a second copy of
anything.

The only client entry point is `RuAgent.Client`. It reads the loopback descriptor
(validating PID and process start time) or starts the Host if none is running.
Neither client ever talks to the Worker or the Broker directly.

## 4. How the design serves performance

**Nothing resident that is not needed.** Generation is a cloud call, so no model,
tokenizer, or KV cache is held in the agent's address space. That removes the
single largest resident allocation a local agent would otherwise carry.

**One generation path, not a pool.** `WorkerV2StepService` receives
`agent.step.start`, calls exactly one provider adapter, and returns one
`AssistantStep`. There is no model router, no queue, and no second transport, so
there is no second set of buffers and no idle worker to keep warm.

**The Worker has a fixed address.** It binds 8005; the Broker takes whatever free
loopback port the Host assigns through the descriptor. No discovery, no scanning,
no polling loop to find a peer.

**Children die with their parent.** One Windows Job Object with `KillOnJobClose`
owns Worker and Broker. A crashed or killed Host takes its whole tree with it, so
no orphaned process keeps holding memory between runs. Each terminal call gets its
own Job Object for the same reason.

**Every tool call is bounded.** `read` caps at 256 KiB, `list` at 512 entries,
`grep` at 64 results and 1 MiB per file, `glob` at 64 results, `bash` output is
head/tail truncated, and terminal calls are killed on timeout or cancellation. A
pathological input degrades into a bounded result instead of a memory spike.

**Context is compacted before it is sent.** `ContextCompactionPlanner` measures
the token budget each step and replaces compacted ranges, instead of growing the
request until the provider refuses it.

**No silent fallback.** One provider, one model, one protocol per run. Retrying
another provider is both a latency cost and a second set of buffers, so it is not
done behind the user's back.

## 5. Agent tools

**10 tools**, declared immutably in `RuAgent.Protocol`
(`AgentToolDefinitionCatalog`). The Host projects the same
id/version/scope/schema/digest into both the prompt and the Worker's
`model_tools`, so there is no second copy of a definition. Every schema is strict
(`additionalProperties: false`).

| Tool | Arguments | Purpose |
| :--- | :--- | :--- |
| `read` | `path`, `offset`, `limit` | Read one file, paged by line. |
| `list` | `path`, `max_entries` (1–512) | List one directory with metadata. |
| `grep` | `query`, `path`, `file_glob`, `case_sensitive`, `is_regex`, `max_results` (1–64) | Search file contents. |
| `glob` | `pattern`, `path`, `max_results` (1–64) | Find files by pattern. |
| `help` | `query` (1–256), `max_results` (1–5) | Search five bundled offline documents. |
| `write` | `op=write\|delete\|restore`, … | Write, delete with a recovery copy, or restore. |
| `edit` | `path`, `old_string`, `new_string` | Replace an exact string. |
| `bash` | `mode=run\|shell`, … | Run an executable or script with `timeout_ms`. |
| `web_search` | `query` (1–512), `max_results` (1–10) | Search the web; approval each time. |
| `web_fetch` | `url` (≤2048) | Fetch one page; approval each time. |

`read`, `list`, `help`, `grep`, and `glob` never prompt for approval. `write`,
`edit`, and `bash` do. `web_search` and `web_fetch` always do.

## 6. Requirements

- **Windows 10 or 11, 64-bit** (22H2 or 24H2 recommended)
- **.NET SDK 10.0** (x64) — to build; published output is self-contained
- **Python 3.11–3.14** — for repository tooling only, never part of the product
- **Visual Studio 2022 C++ Build Tools** if building native components from source

## 7. Build and run

Run every command from `%RU_ROOT%` (the directory holding `build.bat`).

```powershell
# Repository tooling (optional; only the Python checks need it)
.venv\Scripts\python.exe -m pip install -e ".[dev,tooling]"

# Build everything (Release, self-contained, win-x64)
.\build.bat
```

`build.bat` publishes the GUI, Worker, Broker, RuBackend, and CLI as
self-contained `win-x64` executables.

```powershell
# GUI
.\start_dev.bat
# or launch the published build directly:
# gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe

# Terminal UI
dotnet run --project backend-dotnet\RuAgent.Cli\RuAgent.Cli.csproj -- agent chat
```

The GUI and CLI start-or-attach: if a Host is already running they attach to it,
otherwise they start one, and that Host owns the Worker and Broker in a single
Job Object.

## 8. Testing

```powershell
.\scripts\test_dotnet.ps1 -Configuration Debug
```

The script discovers **12 test projects** under `backend-dotnet` and
`gui-winui3.Tests` (excluding `bin`/`obj`/`out`), runs them in sequence, and stops
at the first failure. Current state: **3,262 tests, 0 failing**.

Also available:

```powershell
# Repository guards: documentation links, route contract, build script
.venv\Scripts\python.exe -m pytest tests -q

# The route contract must match backend-dotnet/public-route-contract.json
.venv\Scripts\python.exe scripts\check_aspnetcore_route_matrix.py
```

`public-route-contract.json` is the source of truth for the **37 HTTP routes**
(17 non-Agent plus 20 under `/agent/v1/*`). A test compares the live
`EndpointDataSource` against that file, so adding or removing a route without
updating it fails CI.

Test counts move with each gate; see
[verification](docs/operations/verification.md) for the latest.

## 9. Project layout

```text
.\
├── backend-dotnet/                    # .NET 10 core
│   ├── RuBackend/                     # ASP.NET Core Public Host (:8000)
│   ├── RuAgent.Broker/                # Tool execution, policy, approvals
│   ├── RuAgent.Runtime.Worker/        # Worker v2 (:8005), provider adapters
│   ├── RuAgent.Runtime.Adapters/      # Three provider dialects
│   ├── RuAgent.Harness.Core/          # State machines, prompt, tool pipeline
│   ├── RuAgent.Harness.Persistence/   # SQLite schema v7, AES-GCM payloads, outbox
│   ├── RuAgent.Protocol/              # 10 tool definitions, envelopes, contracts
│   ├── RuAgent.Hosting/               # Job Object, descriptor, single instance
│   ├── RuAgent.Client/                # Shared client library
│   ├── RuAgent.Cli/                   # Terminal UI
│   ├── RuAgent.Connectors/            # Calendar/task/mail (not advertised as tools)
│   └── *.Tests/                       # 11 test projects
├── gui-winui3/                        # WinUI 3 desktop app
│   ├── Views/                         # ChatView, SettingsView, Diagnostics, LogView
│   ├── Services/                      # ApiService, AgentRunController, *Policy modules
│   └── Controls/                      # MarkdownBlock, TerminalPanel, ApprovalPanel
├── gui-winui3.Tests/                  # Unit and source-contract tests
├── gui-winui3.Harness/                # GUI acceptance through UI Automation
├── tests/                             # Python guards (routes, docs, build, cleanup)
├── scripts/                           # test_dotnet.ps1, launcher, route checks
├── skills/                            # Example SkillManifest packages
├── docs/                              # Architecture, operations, ADRs
├── backend-dotnet/public-route-contract.json   # The 37 public routes
├── build.bat                          # Release self-contained build
└── start_dev.bat                      # Development launch
```

## 10. Security and data

Performance is not paid for with permissions:

- **Data** lives in `%LOCALAPPDATA%\Ru`. `RU_DATA_ROOT` and `RU_LOG_ROOT`
  isolate it completely for tests and CI.
- **API keys** are stored through Windows Credential Manager (DPAPI) on the Host
  side. The GUI holds no credential: a key is typed into a `PasswordBox` and
  posted straight to `/settings`.
- **Sensitive payloads** in `agent_harness.db` are AES-GCM 256 encrypted. The key
  is DPAPI-protected (`harness_key.dat`) and fails closed — losing it means the
  payloads cannot be read, and there is no fallback.
- **Interactive approval** is required for outside-workspace access
  (`external_directory_required`), deletion
  (`terminal_delete_requires_approval`), unknown executables
  (`terminal_unknown_executable`), outside-workspace mutation
  (`terminal_external_mutation_requires_approval`), extended timeouts
  (`terminal_timeout_requires_approval`), and web access
  (`web_approval_required`).
- **Permissions are session-scoped.** Every Agent run starts at the `standard`
  preset; `trusted` is opt-in per session with interactive confirmation. An
  outside-folder approval opens a grant for the current session only, terminal
  tokens are single-use, and **no grant survives a restart**. There is no
  permanent permission.
- **Workspace trust** binds to volume serial and root file identity, not to a
  marker file inside the repository.

## 11. Not done yet

Stated plainly so the sections above are not read as stronger claims than they are:

- **Not ahead-of-time compiled.** `PublishReadyToRun` is `False` in the WinUI
  project for both configurations and Native AOT is not enabled. The shipped
  assemblies are managed IL, JIT-compiled at first use. Enabling precompilation is
  the most direct remaining step toward the performance goal.
- **No performance gate in CI.** `ci.yml` runs the 12 test projects, the route
  contract, and the repository guards. Nothing measures or limits CPU, memory, or
  GPU cost, so the focus described here is a design commitment rather than an
  enforced property.
- **No recorded baseline.** The repository contains no measurement of the
  runtime's own footprint.
- Out of scope for now: subagent orchestration, LSP integration, MCP discovery,
  and an OS-level filesystem/network sandbox.
