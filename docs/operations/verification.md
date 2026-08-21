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

The compatibility harness is the deterministic public-edge regression gate. It
uses a temporary data root, probes safe GET routes with fail-closed status
checks, verifies chat privacy/order, and always verifies child and port cleanup:

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --deterministic-chat --output D:\LLM\artifacts\aspnetcore\deterministic-chat-gate.json
```

The live voice gate is optional because it requires OmniVoice and a real
audio-producing worker:

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --require-voice-audio --benchmark-samples 20
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
