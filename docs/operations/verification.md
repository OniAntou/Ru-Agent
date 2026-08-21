# Verification

Run heavyweight gates sequentially on this Windows workspace. A fresh worktree
must restore each .NET test project before using `--no-restore`.

```powershell
dotnet restore D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo
dotnet restore D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo
```

The canonical automated gates are:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
dotnet list D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj package --vulnerable --include-transitive
cmd /c D:\LLM\build.bat
```

The route checker must report 41 HTTP routes and 2 WebSocket routes. The build
must produce self-contained `RuBackend.exe` and `RuApp.exe`; verification does
not launch them.

The backend Harness vNext is the deterministic public-edge regression gate. It
owns a temporary worker/edge session, leases free loopback ports, generates
memory-only desktop and worker bearers, classifies every route, and always
verifies child and port cleanup:

```powershell
$artifact = Join-Path $env:TEMP 'ru-harness-vnext-backend.json'
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output $artifact
```

The artifact uses schema version 2 and records only bounded statuses, reason
codes, timings, counts, route coverage, and cleanup booleans. The expected
coverage is 41 HTTP routes and 2 WebSocket routes. Observation routes are
explicitly `skipped` with `hardware_or_external_observation_required`; all
other safe routes use isolated fixtures. Auth ownership must show the generated
bearer succeeding and both wrong/missing bearers receiving 401/403. A child
that exits after Harness stop is recorded as `expected_stop`, including a
non-zero shutdown code.

`--deterministic-chat` remains a compatibility alias for
`--mode backend-deterministic`. The backend mode uses dynamic ports and
synthetic PCM WAV/STT/TTS; it never requires a model, microphone, speaker, or
VTube Studio connection.

The production live voice gate is separate because it requires OmniVoice and a
real audio-producing worker:

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode runtime-live --require-voice-audio --benchmark-samples 20
```

Automated voice tests prove protocol ordering, cancellation, cleanup, metadata
privacy, non-empty WAV frames, VAD boundaries, capture lifetime, the
hands-free state machine, stale-transcription suppression, and the Chat
integration contract. They do not prove that speakers sound correct, a
physical microphone transcribes correctly, or a live VTube Studio model reacts
as intended.

The separate hands-free hardware gate is user-run: start the Chat microphone,
speak two Vietnamese or mixed Vietnamese-English turns, confirm one automatic
send per turn and one audible reply per turn, confirm the microphone stays
closed while Ru speaks, then stop during listening/STT/playback and verify no
late prompt or audio is produced. Repeat once with subtitles and VTube Studio
enabled. This gate is intentionally not represented as a passing automated
test.
