# Design: Cloud-First Settings Fix — Ollama/LM Studio/Custom Save Path + GGUF Retirement

Date: 2026-08-18
Status: Approved (user), pending spec review

## Background

Commit `ecee44b` switched Ru to cloud-first generation with OpenAI-compatible local
servers (Ollama, LM Studio, Custom). The GUI offers these backends but the settings
save path is broken end-to-end at the .NET layer:

- `SettingsTransitionService.NormalizeBackend` throws `SettingsPayloadException`
  for `ollama` / `lmstudio` / `custom` → GUI shows "Lưu thất bại".
- The worker's `runtime_load` handler rejects the same backends.
- The model/URL fields for these backends are never persisted, forwarded, or returned
  by .NET; `custom_api_key` is a phantom setting read in `status.py` but never stored.
- `RequiresRuntimeLoad` omits the new keys, so URL/model changes never reload the worker.

User decisions (confirmed):

1. Local model (GGUF/llama.cpp) is intentionally dropped. Keep the `llama_cpp.py`
   module code (dormant), but remove llama.cpp from the runtime flow: old settings
   files with `llama.cpp`/`gguf` migrate to OpenCode Zen automatically.
2. Keep Ollama, LM Studio, and Custom OpenAI as selectable backends — fix the save path.
3. Add `custom_api_key` end-to-end as a credential (Windows Credential Manager),
   like OpenCode/Kilo.
4. Delete the GGUF-only scripts: `scripts/build_ru_v1_1.py`,
   `scripts/build_ru_v1_2.py`, `scripts/evaluate_ru_lfm2_5_v1.py`, and the configs
   only they reference: `configs/ru_lfm2_5_v1_1.json`, `configs/ru_lfm2_5_v1_2.json`.
5. `get_status()` must report real model load state instead of always `"ok"`.

## Approach

**Explicit `SettingsSnapshot` fields + credential-backed custom API key.**
No reliance on raw JSON passthrough (the exact mechanism that let the bug slip
through the green test suite).

## Changes

### .NET (RuBackend)

1. `Business/Settings/SettingsSnapshot.cs` — add six fields:
   `OllamaModel`, `LmStudioModel`, `CustomModel`, `OllamaUrl`, `LmStudioUrl`, `CustomUrl`.
2. `Business/Settings/SettingsTransitionService.cs`:
   - `NormalizeBackend`: accept `ollama` → `"Ollama"`, `lmstudio` → `"LM Studio"`,
     `custom` → `"Custom OpenAI"`; map `gguf`/`llama.cpp` → `"OpenCode Zen"`
     (migration, no longer reject).
   - `BuildCandidate`: read the six new keys from the payload
     (`ollama_model`, `lmstudio_model`, `custom_model`, `ollama_url`,
     `lmstudio_url`, `custom_url`), falling back to current snapshot values.
   - `BuildCredentialUpdates`: add `custom_api_key` account
     (`CustomAccount = "custom_api_key"`).
   - `RequiresRuntimeLoad`: include the six new keys + `custom_api_key`.
3. `Business/Settings/SettingsStore.cs`:
   - `CredentialFields` += `"custom_api_key"` (stripped from the persisted file).
   - `ReadUnlockedAsync` / `SaveAsync` / `Validate`: read/write/validate the six new
     fields; `NormalizeBackend` maps `gguf`/`llama.cpp` → `BackendOpenCode`.
4. `Business/Settings/LoopbackRuntimeSettingsLoader.cs` — forward the six new keys
   in the `runtime_load` payload.
5. `Business/BusinessEndpoints.cs` — `ToSettingsResponse`: emit the six new keys,
   strip `custom_api_key`, add `has_custom_api_key` (pattern like OpenCode/Kilo).
6. `RuBackend.Tests` — new tests:
   - Full save round-trip for `ollama`, `lmstudio`, `custom` (payload → transition
     → store → response) with model/URL fields preserved.
   - `custom_api_key` stored as credential, stripped from settings file.
   - `gguf`/`llama.cpp` payload migrates to `OpenCode Zen`.
   - `RequiresRuntimeLoad` triggers on new keys.

### GUI (gui-winui3)

7. `Models/Models.cs` — `AppSettingsDto`: add `CustomApiKey` (`custom_api_key`) and
   `HasCustomKey` (`has_custom_api_key`).
8. `Views/SettingsView.xaml` — add a password row "Custom API Key" (visible only for
   backend `custom`), same pattern as OpenCode/Kilo rows.
9. `Views/SettingsView.xaml.cs`:
   - Load: restore key placeholder ("Đã lưu (để trống để giữ nguyên)") via `HasCustomKey`.
   - Save: include `CustomApiKey` in the DTO.
   - `UpdateVisibilityForBackend`: show the custom key row only for `custom`.

### Python (ru_runtime)

10. `src/ru_runtime/worker/v1/core.py` — add
    `self.custom_cred = CredentialStore("Ru", "custom_api_key")`.
11. `src/ru_runtime/worker/v1/handlers.py` — `runtime_load`:
    - Accept `"Ollama"`, `"LM Studio"`, `"Custom OpenAI"` (remove `"llama.cpp"`).
    - Map per-backend model (`ollama_model` / `lmstudio_model` / `custom_model`)
      and base URL (`ollama_url` / `lmstudio_url` / `custom_url`).
    - `api_key`: OpenCode/Kilo from their credential stores (unchanged);
      Custom from `core.custom_cred`.
    - Pass `base_url` through `runner.load`.
12. `src/ru_runtime/worker/v1/runtime_services.py` — `_BACKEND_ALIASES`:
    `"gguf"` and `"llama.cpp"` map to `BACKEND_OPENCODE` (old settings never load
    GGUF; `apply_saved_settings`'s GGUF branch becomes unreachable).
13. `src/ru_runtime/generation/runner.py` — add `self.load_error: str | None = None`;
    set it at the start of `load()` and in a try/except around the load body
    (record message, re-raise). Clear on success. No behavior change otherwise.
14. `src/ru_runtime/worker/v1/status.py`:
    - `get_status()`: return `{"status": "error", "message": <load_error>}` when
      `runner.load_error` is set; `"ok"` only when the active backend loaded.
    - `get_models()` custom branch: read `custom_api_key` from `core.custom_cred`
      instead of the phantom settings key.
15. Python tests:
    - `tests/test_worker_runtime_handlers.py` (or sibling): `runtime_load` succeeds
      for Ollama/LM Studio/Custom with correct model/URL/key wiring; rejects nothing
      unknown; `llama.cpp` rejected.
    - `tests/test_runtime_services.py` or `test_headless_runner.py`: gguf alias
      resolves to OpenCode Zen; `apply_saved_settings` never attempts GGUF.
    - `tests/test_worker_server.py`/status tests: `get_status` reflects load errors.
    - Update any test that asserts the old backend whitelist or phantom key behavior.

### Scripts / configs (deleted)

16. Delete: `scripts/build_ru_v1_1.py`, `scripts/build_ru_v1_2.py`,
    `scripts/evaluate_ru_lfm2_5_v1.py`, `configs/ru_lfm2_5_v1_1.json`,
    `configs/ru_lfm2_5_v1_2.json`.
    Verified: the only references to the configs are the deleted scripts;
    `evaluate_ru_model.py` + `ru_judge_eval.json` remain (live eval path).
    Check docs/README for dangling references to the deleted scripts and update if
    trivial (e.g. verification.md), otherwise flag in the plan.

## Out of scope

- `server.py` HELLO_OK `"observation"` capability claim (user declined).
- Chat flicker / VTS / judge / eval-cases improvements (unrelated to this fix).
- Removing `llama_cpp.py` / GGUF code paths in `runner.py` (kept dormant).

## Error handling

- Save failures keep the existing transactional behavior: credentials restored,
  runtime rolled back to previous snapshot, one redacted message to the GUI.
- `runtime_load` failures for the new backends surface the worker's error code
  through the existing `WorkerProtocolException` path.
- Missing `custom_api_key` on an authed custom endpoint: model fetch returns the
  existing "API key is not set" style error via `get_models`.

## Testing strategy

- .NET: `dotnet test` on RuBackend.Tests (new round-trip + migration + credential tests).
- Python: pytest run of the touched suites (worker handlers, runtime services,
  runner, status) — real httpx calls for `fetch_models` where the existing suite
  already does so; no mocks of the unit under test.
- GUI: existing WinUI test project run; new DTO fields are additive.

## Success criteria

- Selecting Ollama / LM Studio / Custom in the GUI saves successfully ("Đã lưu"),
  survives restart, and reloads the worker with the right model + URL + key.
- Old settings files with `llama.cpp`/`gguf` never attempt a GGUF load; they migrate
  to OpenCode Zen and the GUI shows OpenCode.
- `/status` reports an error state when model load failed at startup or after reload.
- All three test suites pass.
