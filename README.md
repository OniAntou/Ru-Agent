# Ru Agent

A coding agent that runs as native processes on Windows. The overriding goal is
**machine-resource efficiency**: a coding agent that is fast to talk to but holds
a gigabyte of resident memory and a busy CPU costs the user more than one that
stays near idle between turns.

---

## Contents

- [1. The goal](#1-the-goal)
- [2. What we measure](#2-what-we-measure)
- [3. What runs on the machine](#3-what-runs-on-the-machine)
- [4. How the design serves the goal](#4-how-the-design-serves-the-goal)
- [5. Agent tools](#5-agent-tools)
- [6. Requirements](#6-requirements)
- [7. Build and run](#7-build-and-run)
- [8. Measuring resource cost](#8-measuring-resource-cost)
- [9. Testing](#9-testing)
- [10. Project layout](#10-project-layout)
- [11. Security and data](#11-security-and-data)
- [12. Not done yet](#12-not-done-yet)

---

## 1. The goal

This is a desktop coding agent. It sits next to the user's editor, terminal, and
browser for hours at a time, and it competes with them for RAM, CPU, and GPU. So
the number that matters is not how quickly a token appears on screen — a cloud
provider mostly decides that — but **what the agent costs the machine while it is
open, and while it is working.**

The target is that an idle Ru costs almost nothing, and a working Ru costs only
what the actual work needs.

| Principle | How it is done |
| :--- | :--- |
| No interpreter runtime | The whole system is .NET 10. No Python, no Node, no sidecar process to keep warm. |
| No in-process model | Generation calls the configured cloud provider over HTTP/SSE. No weights in memory, no engine to load, no GPU allocation for inference. |
| One worker, not a pool | Exactly one managed Worker process serves `/worker/v2`. Nothing else in the tree can generate text, so the cost does not scale with sessions. |
| Bounded reads, always | Every tool that touches a file has a hard cap, so one large directory cannot turn into a memory spike. |
| Proactive context compaction | The prompt budget is planned before each step instead of sending the whole history and letting the provider reject it. |
| Children die with the parent | One Windows Job Object with `KillOnJobClose` owns Worker and Broker, so no orphaned process keeps holding memory between runs. |
| No silent fallback | One provider, one model, one protocol per run. Retrying another provider is both a latency cost and a second set of buffers. |

## 2. What we measure

Resource cost, per process tree, in three dimensions:

| Dimension | Metric | Why it is the one that matters |
| :--- | :--- | :--- |
| Memory | Working set (MiB) and private bytes (MiB) | Working set is what the user loses from available RAM. Private bytes catches leaks that the working set hides after trimming. |
| CPU | Percent of the whole machine, normalised across logical processors | 100% means the tree saturated every core. A per-core figure flatters a multi-threaded tree. |
| GPU | Utilization percent, Windows GPU Engine counters | An agent that is not doing inference should not be waking the GPU. |

All three are reported as p50/p95/p99/max over a sampling window, because an
average hides the spike that actually makes a machine feel slow.

**This is not a latency benchmark.** Time-to-first-token is dominated by the
provider and by the network, which this repository does not control. The numbers
here describe the agent's own footprint.

## 3. What runs on the machine

```text
RuApp.exe (WinUI 3)                 RuAgent.Cli.exe (TUI)
  Mica/Acrylic · streaming md         REPL · slash commands
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

Four processes at steady state: the UI, the Host, one Worker, one Broker. The
Worker does not spawn per session and the Broker does not spawn per request, so
opening a second chat does not double the footprint.

The only client entry point is `RuAgent.Client`. It reads the loopback
descriptor (validating PID and process start time) or starts the Host if none is
running. Neither client ever talks to the Worker or the Broker directly.

## 4. How the design serves the goal

**Nothing is resident that is not needed.** Generation is a cloud call, so no
model, tokenizer, or KV cache is held in the agent's own address space. That
removes the single largest resident allocation a local agent would otherwise
carry.

**One generation path.** `WorkerV2StepService` receives `agent.step.start`, calls
exactly one provider adapter, and returns one `AssistantStep`. There is no model
router, no queue, and no second transport — so there is no second set of buffers.

**The Worker has a fixed address.** It binds 8005 and the Broker takes whatever
free loopback port the Host assigns through the descriptor, so no process
discovery or polling loop is needed to find them.

**Every tool call is bounded.** `read` caps at 256 KiB, `list` at 512 entries,
`grep` at 64 results and 1 MiB per file, `glob` at 64 results, `bash` output is
head/tail truncated, and every terminal call runs inside its own Job Object that
is killed on timeout or cancellation. A pathological input degrades into a
bounded result instead of a memory spike.

**Context is compacted before it is sent.** `ContextCompactionPlanner` measures
the token budget each step and replaces compacted ranges, rather than growing the
request until the provider refuses it.

**Terminal work does not leak processes.** `StreamingProcessRunner` drains stdout
and stderr concurrently into a head/tail buffer and kills the entire process tree
on cancel or timeout, so a hung command cannot accumulate children.

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
- **.NET SDK 10.0** (x64)
- **Python 3.11–3.14** — for test and repository tooling only, never the runtime
- **Visual Studio 2022 C++ Build Tools** if building native components from source

## 7. Build and run

Run every command from `%RU_ROOT%` (the directory holding `build.bat`).

```powershell
# Repository tooling (optional; only needed for the Python checks)
.venv\Scripts\python.exe -m pip install -e ".[dev,tooling]"

# Build everything (Release, self-contained)
.\build.bat
```

`build.bat` publishes the GUI, Worker, Broker, RuBackend, and CLI as
self-contained win-x64 executables.

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

## 8. Measuring resource cost

Measure a running tree by name or pid. The script never terminates the target.

```powershell
# Steady state, 60 seconds, gated at 250 MiB working set
.\scripts\measure_resources.ps1 -Name RuBackend -DurationSeconds 60 `
    -WarmupSeconds 5 -MaxWorkingSetMb 250 `
    -Output artifacts\resources\backend-idle.json

# Under load, while a run is in flight
.\scripts\measure_resources.ps1 -Name RuBackend -DurationSeconds 120 `
    -Output artifacts\resources\backend-load.json
```

It prints a JSON report and, when a limit is given, exits `2` if the p95 exceeds
it — so the same script works as a CI gate:

```json
{
  "cpu_percent":      { "sample_count": 60, "p50": 0.4, "p95": 3.1, "p99": 8.6, "max": 12.2 },
  "working_set_mb":   { "sample_count": 60, "p50": 118.2, "p95": 131.7, "p99": 140.0, "max": 143.9 },
  "private_bytes_mb": { "sample_count": 60, "p50": 124.9, "p95": 138.4, "p99": 147.2, "max": 151.0 },
  "process_count":    { "sample_count": 60, "p50": 4, "p95": 4, "p99": 4, "max": 4 },
  "gpu":              { "supported": true, "source": "windows_gpu_engine", "reads": 6,
                        "utilization_pct": { "p50": 0, "p95": 0, "max": 0 } }
}
```

Those values are the report's shape, not a captured baseline. **This repository
does not yet contain a recorded Ru resource baseline** — see
[Not done yet](#12-not-done-yet). Run the script against a published build to
establish one for your machine.

Three details worth knowing before comparing two runs:

- **Windows PowerShell 5.1 is sufficient.** PowerShell 7 is not required and is
  not installed by the build.
- **GPU sampling is throttled** to one read per `-GpuEverySamples` (default 10)
  because a single GPU Engine enumeration costs about 1.6 seconds on a 16-core
  machine. Raise it for long runs, or set `1` when GPU resolution matters more
  than CPU sample count.
- **A machine without GPU Engine counters reports `gpu.supported: false` with a
  reason.** That is a statement about the machine, not a passing measurement.

## 9. Testing

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

## 10. Project layout

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
├── scripts/                           # test_dotnet.ps1, measure_resources.ps1, launcher
├── skills/                            # Example SkillManifest packages
├── docs/                              # Architecture, operations, ADRs
├── backend-dotnet/public-route-contract.json   # The 37 public routes
├── build.bat                          # Release self-contained build
└── start_dev.bat                      # Development launch
```

## 11. Security and data

Resource efficiency is not paid for with permissions:

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

## 12. Not done yet

Stated plainly so the sections above are not read as stronger claims than they are:

- **There is no recorded resource baseline.** `scripts/measure_resources.ps1`
  exists and is verified against gates, a missing target, and an unsupported-GPU
  machine, but no measurement of a published Ru build has been captured into the
  repository yet. The JSON in section 8 shows the report's shape, not results.
- **CI does not gate resource cost.** `ci.yml` runs the 12 test projects, the
  route contract, and the repository guards. It does not call
  `measure_resources.ps1`. The resource goal is currently a design commitment,
  not an enforced property.
- **GPU coverage is one source.** Only Windows GPU Engine counters are read.
  Machines without them report `supported: false` rather than a number.
- Out of scope for now: subagent orchestration, LSP integration, MCP discovery,
  and an OS-level filesystem/network sandbox.
