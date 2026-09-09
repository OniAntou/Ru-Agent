# Python Dependency Ownership

Last reviewed: 2026-09-01.

This document records the consumer inventory, ownership boundaries, and version declarations for Python dependencies across Ru's workspace.

## Dependency Consumer Inventory

| Package | Declared In | Primary Consumer(s) | Status & Rationale |
| --- | --- | --- | --- |
| `torch` | `pyproject.toml` (`[voice]`), `scripts/setup_omnivoice_env.ps1` | `src/ru_runtime/voice/omnivoice_server.py`, OmniVoice runtime | **Active (Voice Provider)**. The voice extra accepts only the evidence-backed 2.8 line; the isolated setup pins `2.8.0+cu128` (CUDA 12.8). This does not constrain installations that do not request `voice`. |
| `torchaudio` | `pyproject.toml` (`[voice]`), `scripts/setup_omnivoice_env.ps1` | `src/ru_runtime/voice/omnivoice_server.py`, audio processing | **Active (Voice Provider)**. The voice extra accepts the matching 2.8 line; setup pins `2.8.0+cu128` alongside Torch. |
| `omnivoice` | `pyproject.toml` (`[voice]`), `scripts/setup_omnivoice_env.ps1` | `src/ru_runtime/voice/omnivoice_server.py` | **Active (Isolated Provider)**. The extra records the installed `0.2.1` floor; it runs in dedicated `omnivoice_env`. |
| `flask` | `pyproject.toml` (`[dev]` and `[voice]`), `scripts/setup_omnivoice_env.ps1` | `src/ru_runtime/voice/omnivoice_server.py` | **Active**. HTTP server for the isolated OmniVoice process; retained in `dev` for Python server tests. |
| `numpy` | `pyproject.toml` (base + `[voice]`), `scripts/setup_omnivoice_env.ps1` | `src/ru_runtime/voice/omnivoice_server.py` (`_as_waveform`, `_waveform_to_wav`) | **Active (Voice Provider)**. Declared in base for legacy tooling compat; the `voice` extra pins it explicitly so the isolated `omnivoice_env` always has it. |
| `soundfile` | `pyproject.toml` (`[dev]` and `[voice]`), `scripts/setup_omnivoice_env.ps1` | `scripts/benchmark_tts.py` (`OmniVoiceBenchmarkEngine.synthesize`) | **Active (Voice/Benchmark)**. Benchmark WAV writes only; `src/ru_runtime/voice/omnivoice_server.py::synthesize_fast` now writes stdlib `wave` bytes via `mkstemp` so production TTS no longer imports it. |
| `httpx` | `pyproject.toml` (`[runtime]` extra) | `scripts/harness`, compatibility benchmark and acceptance tooling | **Active (Tooling/Harness)**. Production model HTTP client is C#. |
| `keyring` | `pyproject.toml` (`[runtime]` extra) | `src/ru_runtime/persistence/credential_store.py` (lazy import) | **Active (Retained Python tooling)**. OS credential-store adapter, separate from managed provider bootstrap. |
| `ddgs` / `duckduckgo_search` | *Removed from pyproject.toml* | None in Python | **Removed**. Managed web ops (`read` with `op=web_search`/`op=web_fetch`) are implemented in C# (`backend-dotnet/RuAgent.Broker/Tools/Web/`). |
| `pytest`, `ruff`, `mypy` | `pyproject.toml` (`[dev]`) | CI, test runner, lint, typecheck | **Active (Dev/CI)**. `pytest-asyncio` is not declared by this project. |

## Torch Version Alignment and Compatibility Decision

1. **Version authority (`scripts/setup_omnivoice_env.ps1`)**:
   - Pins `torch==2.8.0+cu128` and `torchaudio==2.8.0+cu128` from PyTorch's official CUDA 12.8 wheel index.
   - Confined strictly to `omnivoice_env` to isolate GPU runtime and CUDA DLL dependencies from the core tooling environment.

2. **Project Metadata (`pyproject.toml`)**:
    - Declares `torch>=2.8,<2.9`, `torchaudio>=2.8,<2.9`, `omnivoice>=0.2.1`, `flask>=3.1,<4`, `numpy>=1.26`, and `soundfile>=0.12` under the optional `voice` table.
    - The Torch constraints describe the verified provider line, while setup remains the authority for the CUDA-local build (`+cu128`). They do not force an upgrade of the existing `omnivoice_env` installation.

3. **Verification Policy**:
   - Focused metadata tests use `packaging` specifier semantics, including CUDA local tags; they do not import Torch or load models.
   - `pip check` runs on `.venv` and confirms no broken dependencies; it is not fresh-install proof.
   - `-WhatIf` mode on `setup_omnivoice_env.ps1` validates directory and argument constraints without downloading packages or mutating local environments; confirmed install behavior remains unexecuted here.
