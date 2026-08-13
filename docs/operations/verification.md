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

The route checker must report 38 HTTP routes and 2 WebSocket routes. The build
must produce self-contained `RuBackend.exe` and `RuApp.exe`; verification does
not launch them.

The current model-neutral evaluation entrypoint is optional because its LLM
judge can require external credentials. Its deterministic layer can be run
without the judge:

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\evaluate_ru_model.py --config D:\LLM\configs\ru_judge_eval.json --models v1 --suite smoke --no-judge
```

Evaluation output must remain local and must not be treated as an adoption
decision without bilingual conversation review.

Automated voice tests prove protocol ordering, cancellation, cleanup, metadata
privacy, and non-empty audio frames. They do not prove that speakers sound
correct, a physical microphone transcribes correctly, or a live VTube Studio
model reacts as intended. Listening, microphone, and VTS acceptance remain
explicit user-run hardware checks.
