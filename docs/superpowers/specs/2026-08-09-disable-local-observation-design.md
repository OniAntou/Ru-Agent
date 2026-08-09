# Temporarily Disable Local Screen Observation — Design

**Status:** Scope approved by the user (option A) on 2026-08-09.

## Goal

Temporarily remove the local Qwen3-VL screen-observation implementation because it is too slow, while preserving the backend structured-observation and cloud-reaction contracts so a replacement vision model can be connected later.

## Scope

- Remove the active local vision helper, benchmark, download script, pinned manifest, and their dedicated tests.
- Remove the WinUI local screen capture loop, observation toggle, local metrics DTO, and client methods that only control that loop.
- Keep the FastAPI `/observation/*` structured-reaction routes, including cloud reaction, cancellation, acknowledgement, and voice preflight.
- Change the backend no-key vision status from `engine=local` to an explicit disabled state. `/observation/start`, `/stop`, and `/status` remain compatibility endpoints but never claim an active local observer.
- Keep cloud/Gemini credential handling and model-independent reaction code for future use.
- Update README/TODO language so the paused state is explicit.

## Non-goals

- Do not delete the downloaded `models/vision` weights or shared `tools/llama.cpp` binaries in this change.
- Do not remove `src/mini_gpt/screen_reaction.py` or the structured observation payload schema.
- Do not change chat, voice, VTS, memory, or the active LFM2.5 runtime.

## Design

The GUI no longer owns a local observation worker. The settings page keeps a small disabled-state explanation and the existing cloud vision credential row, but exposes no capture button or misleading local metrics. The backend retains the future-facing structured route and responds with `status=disabled` for the local lifecycle endpoints. This keeps the future replacement-model Seam at the HTTP payload boundary without loading a vision model or capturing the desktop.

## Error and lifecycle behavior

- No local observation process is started by the application.
- `/observation/vision-config` returns `{"status":"disabled","engine":"local","message":"Local screen observation is temporarily disabled while waiting for a replacement vision model."}` when no Gemini key is configured.
- `/observation/start` and `/observation/stop` return `status=disabled` and `active=false`.
- `/observation/status` returns `status=disabled`, `active=false`, zero metrics, and `engine=disabled`.
- Existing `/observation/react` remains available for a future structured-observation producer and continues to use the cloud reaction path.

## Verification gates

- Python tests cover the disabled vision configuration and lifecycle response.
- No remaining active source/test/script reference imports the deleted local vision helper.
- `python -m compileall -q src scripts` passes.
- Full Python pytest passes.
- WinUI Release `win-x64` build passes.
- Documentation no longer instructs users to download, benchmark, or start the local Qwen3-VL model.
