# Ru

Ru is a local Vietnamese-English conversational companion built around a
WinUI 3 desktop app, an ASP.NET Core public backend, and a private Python
runtime worker. The local generation default is LiquidAI LFM2.5-2.6B through
CUDA-enabled llama.cpp.

Current maintainer documentation:

- [Architecture](docs/architecture.md)
- [Domain language](CONTEXT.md)
- [Runtime behavior](docs/operations/runtime-behavior.md)
- [Verification](docs/operations/verification.md)
- [Architecture decisions](docs/adr/)

## Runtime topology

`RuApp.exe` owns the process lifecycle. It creates per-run secrets, starts the
private Runtime Worker on loopback port 8001, starts the ASP.NET Core Public
Host on port 8000, waits for public readiness, and places all owned children in
a Windows kill-on-close Job Object.

ASP.NET Core is the only public backend. It owns authentication, validation,
settings and credentials, sessions/history, activity, structured memory, chat
ordering, public errors, and all 38 HTTP plus 2 WebSocket routes. Python is an
authenticated private implementation boundary for model generation, provider
adapters, STT execution, VTube Studio, observation, and the current voice
engine coordination.

The canonical route inventory is
[`backend-dotnet/public-route-contract.json`](backend-dotnet/public-route-contract.json).
There is no public Python HTTP fallback or alternate backend mode.

## Setup

Use the repository's Python 3.13 environment and install the project extras:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pip install -e "D:\LLM[runtime,gguf,dev]"
```

The active local assets stay outside Git:

- `models\gguf\LFM2.5-2.6B-Q4_K_M.gguf` — desktop generation default.
- `models\lfm2.5-2.6b-hf` — native checkpoint retained for LFM2.5 evaluation
  and a future supported fine-tuning path.
- `models\omnivoice` and `omnivoice_env` — isolated local voice runtime.

The repository currently exposes no supported training CLI. The quarantined
training compatibility seam is migrated in the next Architecture v2 plan
before any LFM2.5 fine-tuning workflow is documented as active.

## Build and run

Build the self-contained Windows executables:

```powershell
& D:\LLM\build.bat
```

Run the desktop-owned stack:

```powershell
& D:\LLM\start_dev.bat
```

These commands produce and start `RuApp.exe` plus `RuBackend.exe`. Do not start
the Runtime Worker or voice engine as independent public services.

## Current behavior

- Chat preserves bounded context, bilingual routing, natural Vietnamese
  `anh`/`em`, streamed visible text, and private control-metadata filtering.
- Hosted OpenCode Zen and Kilo Gateway adapters are optional. Credentials are
  stored through Windows Credential Manager.
- Structured memory and reviewed examples reject credentials, URLs, sensitive
  text, and VTube Studio metadata.
- OmniVoice is the local TTS engine. The accepted quality baseline is the
  original voice at native 1.00x speed; non-1.00x native speed still requires
  listening acceptance.
- Google `vi-VN` recognition is the current experimental microphone STT path.
- VTube Studio actions and lip sync remain private runtime metadata.
  `VoiceVolume` is the mouth input; `LLM_MouthOpen` is not used.
- Local screen capture and the former local vision provider are disabled. The
  bounded public observation contract remains for a future provider.

See [runtime behavior](docs/operations/runtime-behavior.md) for invariants and
manual acceptance boundaries.

## Evaluation

Run the current model-neutral deterministic and optional judge suite with:

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\evaluate_ru_model.py `
  --config configs\ru_judge_eval.json --models v1 --suite v5
```

Judge output is local evidence, not an automatic model-adoption decision.
Natural bilingual conversation, correct `anh`/`em`, coherent context, and
human listening remain release gates.

## Verification

Run heavyweight gates sequentially:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
cmd /c D:\LLM\build.bat
```

Automated tests do not establish speaker quality, physical-microphone accuracy,
or live VTube Studio behavior. Those remain user-run hardware checks.

## Data and privacy

Business persistence lives in the canonical `%LOCALAPPDATA%\Ru` location
(settings, sessions, history, activity, and the VTS token). Structured memory
stays project-owned under `data/conversations`. `RU_DATA_ROOT` isolates both
for tests and staging. The transactional migration from the legacy
application-data location completed on 2026-08-17; see
[docs/validation/plan3-task6-migration-evidence.md](docs/validation/plan3-task6-migration-evidence.md).
Existing data is protected and must not be manually moved or deleted.

Visible chat text may cross the private generation boundary. Credentials,
audio bytes, VTS actions, voice timing/control metadata, and transcripts must
not enter logs, cleanup manifests, memory, history, or reviewed examples.

## Project boundaries

Model weights, voice assets, active datasets, credentials, runtime app data,
and local caches are not source files. Do not commit them. Do not treat a model
download, unit-test pass, benchmark score, or historical report as live quality
acceptance.
