# Python Dependency Ownership

Last reviewed: 2026-10-07.

This document records the consumer inventory, ownership boundaries, and version
declarations for Python dependencies across Ru's workspace. After the
2026-09-13 media teardown the repository has no Python product source, so the
only Python dependencies left belong to tooling and CI.

Update 2026-10-08: the product ban on Python was lifted for optional
capabilities (skills declaring `requires: python`); the default path stays
in-process .NET, nothing is bundled, tooling ownership is unchanged, and
bundling an interpreter requires an ADR.

Update 2026-10-07: the working `.venv` was slimmed to the declared tooling set.
116 legacy packages (torch, torchaudio, transformers, opencv, datasets, the
audio/VTube stack and their transitive dependencies) were uninstalled; the
remaining environment is the `[dev]`/`[tooling]` extras plus `pip`/`setuptools`
and the local editable package. `pip check` reports no broken requirements and
the Python guard suite passes with that set.

## Dependency Consumer Inventory

| Package | Declared In | Primary Consumer(s) | Status & Rationale |
| --- | --- | --- | --- |
| `httpx` | `pyproject.toml` (`[tooling]` extra) | `scripts/harness` probes, backend/route tooling, acceptance scripts | **Active (Tooling)**. The production model HTTP client is C#. |
| `pytest`, `ruff` | `pyproject.toml` (`[dev]`) | CI, test runner, lint | **Active (Dev/CI)**. `pytest-asyncio` is not declared by this project. |
| `packaging` | `pyproject.toml` (`[dev]`) | `tests/test_python_metadata.py` specifier assertions | **Active (Dev/CI)**. |
| `torch`, `torchaudio`, `omnivoice`, `flask`, `numpy`, `soundfile` | *Removed from `pyproject.toml`* | None | **Removed**. Voice/STT and OmniVoice were deleted with the media surfaces; the `[voice]` extra, `scripts/setup_omnivoice_env.ps1`, and the `[voice]` dependency set are gone. The isolated `omnivoice_env/` directory and `models/omnivoice/` can still be present on a working copy from before the teardown: they are gitignored build leftovers, read by nothing, and safe to delete. |
| `keyring` | *Removed from `pyproject.toml`* | None | **Removed**. The Python credential-store mirror was deleted with `src/ru_runtime`; Windows Credential Manager is owned by the C# host. |
| `mypy` | *Removed from `pyproject.toml`* | None | **Removed**. `src/ru_runtime` was the only mypy target; the gate had nothing left to check. |
| `SpeechRecognition`, `pyaudio`, `psutil`, `websockets` | *Removed from `pyproject.toml`* | None | **Removed**. The Python speech/voice runtime and its benchmark tooling were deleted. |
| `ddgs` / `duckduckgo_search` | *Removed from pyproject.toml* | None in Python | **Removed**. Managed web tools (standalone `web_search`/`web_fetch`, Google HTML primary with fixed free fallbacks DuckDuckGo/Bing/Mojeek) are implemented in C# (`backend-dotnet/RuAgent.Broker/Tools/Web/`). |

## Verification Policy

- Focused metadata tests use `packaging` specifier semantics; they do not import
  any runtime package.
- `pip check` runs on `.venv` and confirms no broken dependencies; it is not
  fresh-install proof.
- `pip install -e ".[dev,tooling]"` is the CI install step; the project ships no
  packages, so the editable install only resolves those extras.
