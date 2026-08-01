# Ru Desktop

React 19 + Tauri 2 desktop client for Ru. Run from the repository root with
`start_dev.bat`, or from this directory with `npm run tauri dev`.

Tauri owns the complete FastAPI process tree and supplies a per-run loopback
token to the frontend through the `backend_runtime` command. On Windows, the
backend is assigned to a kill-on-close Job Object so closing or crashing Tauri
also terminates OmniVoice, `llama-server`, and any other backend descendants.
Frontend REST and WebSocket code must use `src/lib/backendClient.ts`; do not
add unauthenticated direct calls to port 8000.

The always-mounted sidebar observation control is manual and off on every app
start. It observes only the Windows primary monitor, keeps frames in RAM, and
forces accepted proactive reactions through Ru OmniVoice even when ordinary
chat TTS is disabled. `src/lib/observationClient.ts` is the only frontend
boundary for observation commands/events. Do not add a screen preview, direct
vision-server calls, persistent observation content, or a substitute browser
voice.

Verification:

```powershell
npm test
npm run build
cd src-tauri
cargo test --locked
cargo check --locked
```
