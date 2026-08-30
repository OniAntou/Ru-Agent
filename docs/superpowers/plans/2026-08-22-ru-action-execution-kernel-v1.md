# Ru Action Execution Kernel v1 Implementation Plan

**Status:** Implemented 2026-08-23; native GUI/audio acceptance remains user-owned.
The checkboxes below preserve the approved execution sequence; current
operating truth lives in `docs/operations/agent-tools.md` and
`docs/operations/verification.md`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Ru unable to claim a local action succeeded unless the process-isolated Broker returned a typed, verified receipt, with `system.volume@1.0.0` as the first model-enabled native action in both normal chat and Agent mode.

**Architecture:** Preserve WinUI, the ASP.NET Core Public Host, the private Python Worker Contract, and RuAgentBroker, but replace the Broker's manifest/dispatch split with executable bindings that return one authoritative outcome. The Host obtains tool metadata from the Broker handshake and forwards only the actual grant; the Worker owns selection and truthful rendering while every production Windows side effect remains in the Broker.

**Tech Stack:** Python 3.11–3.14, pytest, .NET 10, ASP.NET Core WebSockets, C# Core Audio COM interop, System.Text.Json, xUnit, WinUI 3, UI Automation Harness, and the existing privacy-safe diagnostic pipeline.

## Global Constraints

- Preserve all unrelated dirty files; stage only the exact files listed by the current task.
- Execute implementation in an isolated worktree created with `superpowers:using-git-worktrees`; do not create it until the user selects an execution option.
- Keep protocol version `1` and the current public HTTP/WebSocket surface unchanged.
- Keep `system.volume@1.0.0` bounded to `get`, `set`, `increase`, `decrease`, `mute`, and `unmute` on the Windows default render endpoint for role `Multimedia`.
- Accept `set.level` only in the inclusive range `0–100`; accept relative `amount` only in the inclusive range `1–50`; legacy `up` and `down` mean four percentage points.
- Execute at most one tool per conversation turn in v1.
- A mutating outcome may be `succeeded` only when `verification_state == "verified"`.
- Never put prompts, replies, raw arguments, output values, scripts, stdout/stderr, credentials, paths, device names, persistent endpoint IDs, or native exception text in diagnostics or Harness artifacts.
- `system.volume@1.0.0` is the only model-enabled tool; file, terminal, system-read, and notes bindings remain Broker-internal.
- `app.open`, `workspace.search`, Web, calendar, task, and mail tools are absent from the executable capability projection.
- Skills remain disabled: advertise no skills, grant no skill IDs, and fail closed on `skill_start`.
- Remove Python Core Audio ownership and the `pycaw`/`comtypes` runtime dependencies.
- Do not edit `gui-winui3/Views/ChatView.xaml`; it has unrelated user-owned changes in the primary checkout.
- Do not run native volume mutation in pytest, xUnit, the deterministic Harness, CI, or the default build.
- Run heavyweight Python, .NET, Harness, and publish gates sequentially on Windows.
- Treat deterministic `contract`, deterministic `adapter`, and opt-in `native` evidence as different claims.
- The final GUI/audio acceptance remains user-owned and must not be inferred from deterministic results.

---

## Baseline and worktree boundary

The approved design is [`2026-08-22-ru-action-execution-kernel-v1-design.md`](../specs/2026-08-22-ru-action-execution-kernel-v1-design.md) at or after commit `dca2605`. The primary checkout is `D:\LLM` on `main` and contains unrelated uncommitted voice, meteor, theme, cleanup, and operations-document changes. The execution worktree must branch from the commit containing this plan and use a `codex/` branch name.

The two primary-checkout files below are intentionally excluded from worktree commits because they are already dirty:

- `D:\LLM\docs\operations\logging.md`
- `D:\LLM\docs\operations\runtime-behavior.md`

Task 11 supplies exact additive sections for those files. Apply them only after code integration, against the primary checkout's current content, and leave them unstaged unless the user separately authorizes committing all overlapping work.

## Worktree bootstrap

After the user chooses an execution mode, invoke `superpowers:using-git-worktrees`, create a clean `codex/ru-action-execution-kernel-v1` worktree from the commit containing this plan, and run every Task 1–11 command with that worktree as the current directory. Repository-relative paths in this plan always mean the isolated worktree, never `D:\LLM`.

- [ ] **Verify isolation and configure the shared Python runtime**

```powershell
$ruWorktree = (git rev-parse --show-toplevel).Trim()
$ruPrimary = 'D:\LLM'
$ruPython = Join-Path $ruPrimary '.venv\Scripts\python.exe'
if ([System.IO.Path]::GetFullPath($ruWorktree) -eq [System.IO.Path]::GetFullPath($ruPrimary)) {
    throw 'Refusing to implement in the dirty primary checkout.'
}
if (-not (Test-Path -LiteralPath $ruPython -PathType Leaf)) {
    throw 'Canonical Ru Python runtime is missing.'
}
if (git status --porcelain) {
    throw 'Execution worktree must start clean.'
}
```

Keep `$ruWorktree`, `$ruPrimary`, and `$ruPython` in the execution PowerShell session; rerun this block after any shell restart. Do not create, copy, or commit a second virtual environment. Pytest resolves the worktree's `src` and `scripts` through `pyproject.toml` while using only the primary checkout's interpreter and installed packages.

- [ ] **Restore every .NET test project before the first `--no-restore` command**

```powershell
dotnet restore backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo
dotnet restore backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --nologo
dotnet restore backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj --nologo
dotnet restore gui-winui3.Tests\gui-winui3.Tests.csproj --nologo
dotnet restore gui-winui3.Harness\RuApp.Harness.csproj --nologo
```

Expected: all restores exit 0 and `git status --porcelain` remains empty. If the primary `.venv` lacks a newly required test-only dependency, stop and report it; do not mutate the primary environment from the isolated implementation task.

## File map

| File | Responsibility after this plan |
| --- | --- |
| `backend-dotnet/RuAgent.Protocol/AgentToolContracts.cs` | Shared terminal statuses, verification states, capability projection, result duration, and privacy-safe endpoint proof fields. |
| `backend-dotnet/RuAgent.Broker/Tools/ToolExecutionOutcome.cs` | One typed adapter outcome and execution context. |
| `backend-dotnet/RuAgent.Broker/Tools/ToolBinding.cs` | Canonical manifest/executor/model-availability/verification binding. |
| `backend-dotnet/RuAgent.Broker/Tools/ToolBindingRegistry.cs` | Atomic registration and lookup of complete executable bindings. |
| `backend-dotnet/RuAgent.Broker/Tools/ToolSchemaValidator.cs` | Bounded object-schema validation before and after adapter execution. |
| `backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs` | Exact list of ten bound internal/executable tools and the safe capability projection. |
| `backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs` | Policy, approval, executor invocation, outcome validation, and authoritative receipt creation. |
| `backend-dotnet/RuAgent.Broker/Tools/Desktop/VolumeTools.cs` | Volume request validation, one-operation endpoint lease, mutation, readback, and verified outcome. |
| `backend-dotnet/RuAgent.Broker/Tools/Desktop/WindowsCoreAudioVolumeController.cs` | Per-operation Core Audio endpoint acquisition, ephemeral endpoint proof, and COM release. |
| `backend-dotnet/RuAgent.Broker/Tools/Notes/NotesAdapter.cs` | Notes output without a conflicting inner status. |
| `backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs` | Capability-bearing hello, validated actual session grants, and disabled skills. |
| `backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs` | Bind capability projection and serialize complete receipts. |
| `backend-dotnet/RuBackend/Agent/AgentModels.cs` | Host capability, grant, and typed receipt models. |
| `backend-dotnet/RuBackend/Agent/AgentBrokerClient.cs` | Parse/cache Broker capabilities, validate grants, and preserve receipt proof. |
| `backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs` | Resolve scope from capability metadata, publish the actual grant, and correlate one tool result. |
| `backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs` | Offer model capabilities and the same tool handler to normal and Agent turns. |
| `backend-dotnet/RuBackend/Conversation/ChatEvent.cs` | Bounded `agent.session` scopes and terminal Agent activity fields. |
| `backend-dotnet/RuBackend/Worker/WorkerModels.cs` | Capability class on intents and verification state on receipts. |
| `backend-dotnet/RuBackend/Worker/WorkerEnvelope.cs` | Strict Worker wire validation for the new fields. |
| `backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs` | Forward receipts and log delivery only after the Worker send succeeds. |
| `src/ru_runtime/worker/v1/action_loop.py` | Bounded volume-intent detector, capability parser, selection validation, receipt validation, and deterministic failure copy. |
| `src/ru_runtime/worker/v1/handlers.py` | Orchestrate one action through the existing private queue without owning side effects. |
| `src/ru_runtime/generation/tool_calls.py` | Strict JSON selection plus legacy volume-marker-to-intent compatibility parsing. |
| `src/ru_runtime/generation/orchestration.py` | Strip private markers without executing desktop actions. |
| `src/ru_runtime/policy/style.py` | Remove app/volume marker advertising; retain only the v1 structured volume instruction. |
| `src/ru_runtime/generation/runner.py` | Preserve raw compatibility intent only during the non-visible selection phase. |
| `src/ru_runtime/system/volume.py` | Deleted; Python no longer owns Core Audio. |
| `gui-winui3/Services/AgentSessionGrantModel.cs` | Bounded GUI model for the actual Host grant. |
| `gui-winui3/Services/AgentSessionService.cs` | Store actual received scopes instead of local hard-coded scopes. |
| `gui-winui3/Services/ChatWebSocketClient.cs` | Parse `agent.session` and reject malformed scope arrays. |
| `gui-winui3/Views/ChatView.xaml.cs` | Render the actual grant through the existing `AgentActivity` element. |
| `backend-dotnet/RuBackend/Diagnostics/DiagnosticEvent.cs`, `DiagnosticEventPolicy.cs`, `DiagnosticCodes.cs`, `IDiagnosticEmitter.cs`, `DiagnosticEmitter.cs` | Bounded tool/verification diagnostic fields and codes. |
| `scripts/harness/artifacts.py` | Explicit Harness evidence class. |
| `scripts/harness/deterministic_worker.py` | Synthetic exact-status tool loop with no native device action. |
| `scripts/harness/session.py` | Isolated workspace fixture and terminal receipt predicate. |
| `scripts/native_volume_acceptance.py` | Explicit opt-in published-Broker mutation, readback, and restoration smoke. |
| `docs/operations/agent-tools.md` | Shipped versus deferred capability table. |
| `docs/operations/verification.md` | Contract/adapter/native commands and claim boundaries. |

---

### Task 1: Replace manifest-only dispatch with truthful executable bindings

**Files:**
- Create: `backend-dotnet\RuAgent.Broker\Tools\ToolExecutionOutcome.cs`
- Create: `backend-dotnet\RuAgent.Broker\Tools\ToolBinding.cs`
- Create: `backend-dotnet\RuAgent.Broker\Tools\ToolBindingRegistry.cs`
- Create: `backend-dotnet\RuAgent.Broker\Tools\ToolSchemaValidator.cs`
- Modify: `backend-dotnet\RuAgent.Protocol\AgentToolContracts.cs`
- Modify: `backend-dotnet\RuAgent.Broker\Tools\ToolCatalog.cs`
- Modify: `backend-dotnet\RuAgent.Broker\Tools\ToolExecutionService.cs`
- Modify: `backend-dotnet\RuAgent.Broker\Tools\Notes\NotesAdapter.cs`
- Modify: `backend-dotnet\RuAgent.Broker\Runtime\BrokerHost.cs`
- Create: `backend-dotnet\RuAgent.Broker.Tests\ToolBindingRegistryTests.cs`
- Create: `backend-dotnet\RuAgent.Broker.Tests\ToolExecutionServiceTests.cs`
- Modify: `backend-dotnet\RuAgent.Broker.Tests\ToolCatalogTests.cs`
- Modify: `backend-dotnet\RuAgent.Broker.Tests\BrokerHostTests.cs`

**Interfaces:**
- Produces `ToolExecutionContext`, `ToolExecutionOutcome`, `ToolExecutor`, `ToolBinding`, and `ToolBindingRegistry` for every later Broker task.
- `ToolExecutor` has the exact signature `ValueTask<ToolExecutionOutcome> (ToolExecutionContext context, JsonObject arguments, CancellationToken cancellationToken)`.
- `ToolExecutionOutcome` carries `Status`, `Output`, `ErrorCode`, `SafeSummary`, `VerificationState`, `EndpointRole`, and `EndpointHash`.
- `ToolCatalog(IReadOnlyDictionary<string, ToolExecutor> executors)` rejects missing or extra executor IDs at startup.
- `ToolExecutionService.ExecuteAsync` returns a `ToolResult` whose outer status is copied from the normalized executor outcome.

- [ ] **Step 1: Write failing binding and outer-status tests**

Add these tests with an `ExecutableStubs()` helper containing exactly the ten IDs shown here:

```csharp
private static Dictionary<string, ToolExecutor> ExecutableStubs() => new(StringComparer.Ordinal)
{
    ["system.time"] = Succeeded,
    ["system.status"] = Succeeded,
    ["system.volume"] = Succeeded,
    ["app.list"] = Succeeded,
    ["files.list"] = Succeeded,
    ["files.read"] = Succeeded,
    ["files.write"] = Succeeded,
    ["terminal.run"] = Succeeded,
    ["terminal.shell"] = Succeeded,
    ["notes.create"] = Succeeded,
};

private static ValueTask<ToolExecutionOutcome> Succeeded(
    ToolExecutionContext context,
    JsonObject arguments,
    CancellationToken cancellationToken) =>
    ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
        new JsonObject(), ToolVerificationStates.NotRequired, "completed"));

[Fact]
public void Catalog_fails_when_a_manifest_has_no_executor()
{
    var executors = ExecutableStubs();
    executors.Remove("files.read");
    var error = Assert.Throws<InvalidOperationException>(() => new ToolCatalog(executors));
    Assert.Equal("incomplete_tool_binding", error.Message);
}

[Fact]
public void Catalog_contains_only_bound_v1_tools()
{
    var ids = new ToolCatalog(ExecutableStubs()).Registry.List()
        .Select(binding => binding.Manifest.Id).ToArray();
    Assert.Equal(new[] {
        "app.list", "files.list", "files.read", "files.write", "notes.create",
        "system.status", "system.time", "system.volume", "terminal.run", "terminal.shell"
    }, ids);
    Assert.DoesNotContain("app.open", ids);
    Assert.DoesNotContain("workspace.search", ids);
    Assert.DoesNotContain("web.search", ids);
    Assert.DoesNotContain("mail.send", ids);
}
```

Add an authenticated Broker test that runs `python -c "raise SystemExit(7)"` through `terminal.run` and asserts:

```csharp
Assert.Equal(ToolResultStatuses.Failed, result.Payload["status"]!.GetValue<string>());
Assert.Equal("process_exit_nonzero", result.Payload["error_code"]!.GetValue<string>());
Assert.False(result.Payload["output"]!.AsObject().ContainsKey("status"));
```

- [ ] **Step 2: Run the focused Broker tests and observe red**

Run:

```powershell
dotnet test backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~ToolBindingRegistryTests|FullyQualifiedName~ToolExecutionServiceTests|FullyQualifiedName~ToolCatalogTests|FullyQualifiedName~BrokerHostTests"
```

Expected: build failures for the new binding/outcome types, followed by the current terminal test showing outer `succeeded` if the type failures are temporarily isolated.

- [ ] **Step 3: Implement the canonical binding and outcome types**

Use these exact public shapes:

```csharp
public static class ToolVerificationStates
{
    public const string NotRequired = "not_required";
    public const string Pending = "pending";
    public const string Verified = "verified";
    public const string Failed = "failed";
    public static IReadOnlySet<string> Known { get; } =
        new HashSet<string>([NotRequired, Pending, Verified, Failed], StringComparer.Ordinal);
}

public sealed record ToolExecutionContext(
    string RequestId,
    string SessionId,
    string? CorrelationId,
    string CapabilityClass,
    int DeadlineMs,
    bool Approved,
    string? ApprovalToken = null);

public sealed record ToolExecutionOutcome(
    string Status,
    JsonObject Output,
    string? ErrorCode,
    string SafeSummary,
    string VerificationState,
    string? EndpointRole = null,
    string? EndpointHash = null,
    int DurationMs = 0)
{
    public static ToolExecutionOutcome Succeeded(
        JsonObject output,
        string verificationState,
        string safeSummary,
        string? endpointRole = null,
        string? endpointHash = null) =>
        new(ToolResultStatuses.Succeeded, output, null, safeSummary,
            verificationState, endpointRole, endpointHash);

    public static ToolExecutionOutcome Failed(
        string errorCode,
        string safeSummary,
        string verificationState = ToolVerificationStates.Failed,
        string? endpointRole = null,
        string? endpointHash = null) =>
        new(ToolResultStatuses.Failed, new JsonObject(), errorCode,
            safeSummary, verificationState, endpointRole, endpointHash);
}

public delegate ValueTask<ToolExecutionOutcome> ToolExecutor(
    ToolExecutionContext context,
    JsonObject arguments,
    CancellationToken cancellationToken);

public sealed record ToolBinding(
    ToolManifest Manifest,
    ToolExecutor Executor,
    bool ModelEnabled,
    string Availability,
    bool VerificationRequired);
```

`ToolBindingRegistry.Register` must validate the manifest through the existing `ToolRegistry`, reject a null executor, and add manifest and binding under one `(Id, Version)` key before exposing it. `ToolSchemaValidator.Validate` must support the existing object-schema keywords `required`, `additionalProperties`, `properties`, `type`, `enum`, `const`, `minimum`, and `maximum`; unsupported schema keywords fail with `unsupported_tool_schema` rather than being ignored.

Build `ToolCatalog` from the exact ten IDs in Step 1. Mark only `system.volume` as `ModelEnabled: true`, `Availability: "native_pending"`, and `VerificationRequired: true`; all other bindings use `ModelEnabled: false`, `Availability: "executable"`, with verification not required. Reject any executor key not in the ten-ID manifest set with `unknown_tool_executor`.

- [ ] **Step 4: Normalize every executor result before creating the receipt**

Replace the `DispatchAsync` return type and unconditional success wrapper with this flow:

```csharp
var stopwatch = Stopwatch.StartNew();
var binding = _catalog.Registry.Get(toolId, toolVersion);
ToolSchemaValidator.Validate(binding.Manifest.InputSchema, arguments);
var outcome = await binding.Executor(
    new ToolExecutionContext(
        request.RequestId,
        request.SessionId,
        request.CorrelationId,
        ReadCapabilityClass(request.Payload),
        request.DeadlineMs,
        approved,
        approvalToken),
    arguments,
    cancellationToken).ConfigureAwait(false);
outcome = Normalize(binding, outcome) with
{
    DurationMs = checked((int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds)),
};
return Receipt(request, outcome);
```

`Normalize` must enforce all of these exact rules:

```csharp
if (!ToolResultStatuses.Known.Contains(outcome.Status))
    return ToolExecutionOutcome.Failed("invalid_executor_status", "Tool returned an invalid status.");
if (!ToolVerificationStates.Known.Contains(outcome.VerificationState))
    return ToolExecutionOutcome.Failed("invalid_verification_state", "Tool verification was invalid.");
if (outcome.Output.ContainsKey("status"))
    return ToolExecutionOutcome.Failed("conflicting_output_status", "Tool returned a conflicting status.");
if (binding.VerificationRequired
    && outcome.Status == ToolResultStatuses.Succeeded
    && outcome.VerificationState != ToolVerificationStates.Verified)
    return ToolExecutionOutcome.Failed("verification_required", "Tool success was not verified.");
if (outcome.Status == ToolResultStatuses.Succeeded || outcome.Output.Count > 0)
    ToolSchemaValidator.Validate(binding.Manifest.OutputSchema, outcome.Output);
return outcome;
```

Before returning, encode `outcome.Output` once and reject it with `output_too_large` when it exceeds `Manifest.MaxOutputBytes`; reject a safe summary longer than 4096 characters with `invalid_safe_summary`. Map known policy, timeout, cancellation, validation, and adapter exceptions to typed outcomes. A final catch maps every other adapter exception to `failed/adapter_error` without using `Exception.Message`. For `approval_required`, use `verification_state: pending`; for non-mutating denial/cancellation/timeout use `not_required` unless a verification attempt already failed.

`Receipt(request, outcome)` sets `AdapterInvoked: true`. Every policy denial, approval-required result, input-schema rejection, missing binding, or pre-dispatch cancellation constructs a receipt with `AdapterInvoked: false`. This private boolean is safe execution provenance for Task 9 and is never sent to the model.

Map `ProcessRunResult.Status` directly to the outer outcome. A non-zero exit uses `failed/process_exit_nonzero`; timeout uses `timed_out/process_timed_out`; cancellation uses `cancelled/process_cancelled`; start failure uses `failed/process_start_failed`. Remove `status` from terminal output. Preserve the existing bounded relative `path` field in `NotesAdapter.Create` and replace its inner `status` with `created: true`. Do not bind `AppTools.Open`.

Extend `ToolResult` with optional trailing fields so direct skill-source tests remain source-compatible:

```csharp
public sealed record ToolResult(
    string RequestId,
    string SessionId,
    string Status,
    JsonObject Output,
    string? ErrorCode,
    string SafeSummary,
    int DurationMs,
    string VerificationState = ToolVerificationStates.NotRequired,
    string? EndpointRole = null,
    string? EndpointHash = null,
    bool AdapterInvoked = false);
```

Serialize those fields, including `adapter_invoked`, in `BrokerHost`, but never serialize raw arguments or exceptions.

- [ ] **Step 5: Run Broker regression tests and commit**

Run:

```powershell
dotnet test backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
```

Expected: all Broker tests pass; the non-zero terminal fixture has outer `failed`, and the exact catalog contains no unbound IDs.

Commit:

```powershell
git add -- backend-dotnet/RuAgent.Protocol/AgentToolContracts.cs backend-dotnet/RuAgent.Broker/Tools/ToolExecutionOutcome.cs backend-dotnet/RuAgent.Broker/Tools/ToolBinding.cs backend-dotnet/RuAgent.Broker/Tools/ToolBindingRegistry.cs backend-dotnet/RuAgent.Broker/Tools/ToolSchemaValidator.cs backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs backend-dotnet/RuAgent.Broker/Tools/Notes/NotesAdapter.cs backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs backend-dotnet/RuAgent.Broker.Tests/ToolBindingRegistryTests.cs backend-dotnet/RuAgent.Broker.Tests/ToolExecutionServiceTests.cs backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs
git commit -m "refactor: make Broker tool outcomes authoritative"
```

---

### Task 2: Make the Core Audio adapter lease and verify one endpoint per operation

**Files:**
- Modify: `backend-dotnet\RuAgent.Broker\Tools\Desktop\VolumeTools.cs`
- Modify: `backend-dotnet\RuAgent.Broker\Tools\Desktop\WindowsCoreAudioVolumeController.cs`
- Modify: `backend-dotnet\RuAgent.Broker\Tools\ToolExecutionService.cs`
- Modify: `backend-dotnet\RuAgent.Broker.Tests\VolumeToolsTests.cs`
- Create: `backend-dotnet\RuAgent.Broker.Tests\EndpointProofTests.cs`
- Create: `backend-dotnet\RuAgent.Broker.Tests\WindowsCoreAudioVolumeControllerTests.cs`
- Modify: `backend-dotnet\RuAgent.Broker.Tests\BrokerHostTests.cs`

**Interfaces:**
- Replaces the lifetime-cached controller with `IVolumeController.OpenDefaultRenderEndpoint() -> IVolumeEndpoint`.
- `IVolumeEndpoint` exposes `EndpointRole`, `EndpointHash`, `GetState`, `SetLevel`, `SetMuted`, and `Dispose`.
- `VolumeTools.Execute(JsonObject arguments) -> ToolExecutionOutcome` returns `verified` only after exact readback through the same endpoint lease.
- Endpoint role is the fixed string `default_render_multimedia`; endpoint hash is 16 lowercase hexadecimal characters salted per Broker process.

- [ ] **Step 1: Write failing lease, readback, and privacy tests**

Replace the current fake controller with a factory/lease fake and add these assertions:

```csharp
[Fact]
public void Set_uses_one_disposable_endpoint_and_returns_verified_proof()
{
    var controller = new FakeVolumeController(level: 40);
    var outcome = new VolumeTools(controller).Execute(new JsonObject
    {
        ["operation"] = "set",
        ["level"] = 2,
    });

    Assert.Equal(ToolResultStatuses.Succeeded, outcome.Status);
    Assert.Equal(ToolVerificationStates.Verified, outcome.VerificationState);
    Assert.Equal("default_render_multimedia", outcome.EndpointRole);
    Assert.Matches("^[a-f0-9]{16}$", outcome.EndpointHash!);
    Assert.Equal(1, controller.OpenCount);
    Assert.Equal(1, controller.DisposeCount);
    Assert.Equal(2, controller.ReadCount);
}

[Fact]
public void Readback_mismatch_never_returns_success()
{
    var outcome = new VolumeTools(new FakeVolumeController(40, applyWrites: false))
        .Execute(new JsonObject { ["operation"] = "set", ["level"] = 2 });
    Assert.Equal(ToolResultStatuses.Failed, outcome.Status);
    Assert.Equal("readback_mismatch", outcome.ErrorCode);
    Assert.Equal(ToolVerificationStates.Failed, outcome.VerificationState);
    Assert.Equal("default_render_multimedia", outcome.EndpointRole);
    Assert.Matches("^[a-f0-9]{16}$", outcome.EndpointHash!);
}
```

In `EndpointProofTests`, call `EndpointProof.Hash("endpoint-id", salt)` twice with the same salt and once with a different salt; assert stable-within-run, different-between-runs, 16 lowercase hex characters, and no raw endpoint substring.

In `WindowsCoreAudioVolumeControllerTests`, use injected native-acquisition and COM-release seams to assert: COM initialization is balanced on the same thread; one successful lease releases endpoint, device, then enumerator exactly once; double disposal is idempotent; failure after each partial acquisition releases only the objects already acquired; and `RPC_E_CHANGED_MODE` does not call `CoUninitialize` for a COM apartment owned by another component.

- [ ] **Step 2: Run focused volume tests and observe red**

```powershell
dotnet test backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~VolumeToolsTests|FullyQualifiedName~EndpointProofTests|FullyQualifiedName~WindowsCoreAudioVolumeControllerTests|FullyQualifiedName~BrokerHostTests"
```

Expected: compile failures because `IVolumeEndpoint`, `OpenDefaultRenderEndpoint`, and `EndpointProof` do not exist.

- [ ] **Step 3: Implement the endpoint lease contract and verified outcome**

Use these exact interfaces:

```csharp
public interface IVolumeController
{
    IVolumeEndpoint OpenDefaultRenderEndpoint();
}

public interface IVolumeEndpoint : IDisposable
{
    string EndpointRole { get; }
    string EndpointHash { get; }
    VolumeState GetState();
    void SetLevel(int level);
    void SetMuted(bool muted);
}
```

In `VolumeTools.Execute`, validate arguments before opening the endpoint, then use exactly one lease:

```csharp
var request = Parse(arguments);
using var endpoint = _controller.OpenDefaultRenderEndpoint();
var before = endpoint.GetState();
if (request.Operation == "get")
{
    return ToolExecutionOutcome.Succeeded(
        Output(request.Operation, before),
        ToolVerificationStates.Verified,
        "Volume state verified.",
        endpoint.EndpointRole,
        endpoint.EndpointHash);
}

Apply(request, before, endpoint, out var expectedLevel, out var expectedMuted);
var after = endpoint.GetState();
if (after.Level != expectedLevel || after.Muted != expectedMuted)
    return ToolExecutionOutcome.Failed(
        "readback_mismatch",
        "Volume change could not be verified.",
        ToolVerificationStates.Failed,
        endpoint.EndpointRole,
        endpoint.EndpointHash);

return ToolExecutionOutcome.Succeeded(
    Output(request.Operation, after),
    ToolVerificationStates.Verified,
    "Volume change verified.",
    endpoint.EndpointRole,
    endpoint.EndpointHash);
```

Return bounded failed outcomes for `unsupported_platform`, `default_endpoint_unavailable`, `read_failed`, `write_failed`, `adapter_error`, and `invalid_arguments`; never copy `Exception.Message` into the outcome.

- [ ] **Step 4: Resolve Core Audio per operation and release COM objects**

Make `WindowsCoreAudioVolumeController` stateless apart from one random 32-byte process salt and an injectable internal native-acquisition seam used only by tests. `OpenDefaultRenderEndpoint` calls `CoInitializeEx` for the current thread, creates an `MMDeviceEnumerator`, resolves `Render/Multimedia`, activates `IAudioEndpointVolume`, obtains the endpoint ID, and returns a synchronous lease. Treat `S_OK` and `S_FALSE` as balanced initialization owned by the lease; treat `RPC_E_CHANGED_MODE` as an already initialized apartment and do not uninitialize it; map every other HRESULT to a stable safe adapter code. No `await` may cross lease acquisition and disposal.

Use a local acquisition guard so a failure after creating the enumerator, resolving the device, activating the endpoint, or reading the ID releases every object already acquired in reverse order. On success, the lease must release every acquired COM object exactly once and call `CoUninitialize` on the same thread only when this operation owns a matching initialization:

```csharp
public void Dispose()
{
    if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
    ReleaseCom(_endpoint);
    ReleaseCom(_device);
    ReleaseCom(_enumerator);
}

private static void ReleaseCom(object? value)
{
    if (value is not null && Marshal.IsComObject(value))
        Marshal.FinalReleaseComObject(value);
}
```

Create endpoint proof without exposing the endpoint ID:

```csharp
public static string Hash(string endpointId, ReadOnlySpan<byte> salt)
{
    var id = Encoding.UTF8.GetBytes(endpointId);
    var input = new byte[salt.Length + id.Length];
    salt.CopyTo(input);
    id.CopyTo(input.AsSpan(salt.Length));
    return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()[..16];
}
```

Update the lazy production binding in `ToolExecutionService` to consume the returned outcome directly. Extend the authenticated fake-controller test to assert `verification_state == "verified"`, fixed role, bounded hash, one endpoint open, and one disposal.

- [ ] **Step 5: Run volume/Broker suites and commit**

```powershell
dotnet test backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
```

Expected: all Broker tests pass without touching the machine's real audio endpoint.

```powershell
git add -- backend-dotnet/RuAgent.Broker/Tools/Desktop/VolumeTools.cs backend-dotnet/RuAgent.Broker/Tools/Desktop/WindowsCoreAudioVolumeController.cs backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs backend-dotnet/RuAgent.Broker.Tests/VolumeToolsTests.cs backend-dotnet/RuAgent.Broker.Tests/EndpointProofTests.cs backend-dotnet/RuAgent.Broker.Tests/WindowsCoreAudioVolumeControllerTests.cs backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs
git commit -m "fix: verify volume through a per-operation endpoint"
```

---

### Task 3: Publish capabilities in the Broker handshake and disable skills

**Files:**
- Modify: `backend-dotnet\RuAgent.Protocol\AgentToolContracts.cs`
- Modify: `backend-dotnet\RuAgent.Protocol\AgentMessageTypes.cs`
- Modify: `backend-dotnet\RuAgent.Broker\Tools\ToolBindingRegistry.cs`
- Modify: `backend-dotnet\RuAgent.Broker\Tools\ToolCatalog.cs`
- Modify: `backend-dotnet\RuAgent.Broker\Tools\ToolExecutionService.cs`
- Modify: `backend-dotnet\RuAgent.Broker\Transport\AgentConnection.cs`
- Modify: `backend-dotnet\RuAgent.Broker\Runtime\BrokerHost.cs`
- Modify: `backend-dotnet\RuAgent.Broker.Tests\ToolCatalogTests.cs`
- Modify: `backend-dotnet\RuAgent.Broker.Tests\BrokerHostTests.cs`
- Modify: `backend-dotnet\RuAgent.Broker.Tests\AgentEnvelopeTests.cs`

**Interfaces:**
- Produces `AgentToolCapability` with exact fields `ToolId`, `ToolVersion`, `Scopes`, `Risk`, `Availability`, and `ModelEnabled`.
- `ToolExecutionService.ListCapabilities() -> IReadOnlyList<AgentToolCapability>` is the only Broker capability source.
- `hello_ok.payload.tools` is a bounded array of those projections; `hello_ok.payload.skills` is always an empty array.
- `session_open` rejects non-empty skill IDs and returns a `session_opened` envelope containing the actual opened scopes.

- [ ] **Step 1: Write failing handshake and disabled-skill tests**

Add an authenticated hello test:

```csharp
var hello = await fixture.HelloAsync("session-capabilities", "corr-capabilities");
var tools = hello.Payload["tools"]!.AsArray();
var volume = Assert.Single(tools, node =>
    node!["tool_id"]!.GetValue<string>() == "system.volume");
Assert.Equal("1.0.0", volume!["tool_version"]!.GetValue<string>());
Assert.Equal("native_pending", volume["availability"]!.GetValue<string>());
Assert.True(volume["model_enabled"]!.GetValue<bool>());
Assert.Equal("system.write", Assert.Single(volume["scopes"]!.AsArray())!.GetValue<string>());
Assert.Empty(hello.Payload["skills"]!.AsArray());

var ids = tools.Select(node => node!["tool_id"]!.GetValue<string>()).ToArray();
Assert.DoesNotContain("app.open", ids);
Assert.DoesNotContain("web.search", ids);
Assert.DoesNotContain("calendar.create", ids);
Assert.DoesNotContain("mail.send", ids);
```

Add a session-open test sending `skill_ids: ["research-note"]`; assert outer `denied`, error `skills_disabled`, and `BrokerSession.SkillIds` remains empty. Add a successful open test that requests `system.write` and asserts the response contains exactly `scopes: ["system.write"]` and `skill_ids: []`.

Add `AgentMessageTypes.SessionOpened = "session_opened"` to the protocol known set and its envelope round-trip test. Preserve protocol version 1 because this is an additive private message type and Broker/Host ship atomically.

- [ ] **Step 2: Run handshake tests and observe red**

```powershell
dotnet test backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~ToolCatalogTests|FullyQualifiedName~BrokerHostTests|FullyQualifiedName~AgentEnvelopeTests"
```

Expected: hello has no `tools`/`skills`, and session-open currently accepts skill IDs.

- [ ] **Step 3: Add the shared capability record and safe projection**

Add:

```csharp
public sealed record AgentToolCapability(
    string ToolId,
    string ToolVersion,
    IReadOnlySet<string> Scopes,
    string Risk,
    string Availability,
    bool ModelEnabled);
```

`ToolBindingRegistry.ListCapabilities` maps each complete binding, sorts by tool ID/version, lowercases the `RiskClass` name (`read`, `reversible`, `external`, or `destructive`), and deep-copies scopes into an ordinal set. `ToolExecutionService.ListCapabilities` delegates to it. It returns no skill package metadata.

- [ ] **Step 4: Bind capability hello and actual grants**

Extend `AgentConnection` with these constructor inputs:

```csharp
Func<IReadOnlyList<AgentToolCapability>> capabilityProvider,
IReadOnlySet<string> executableScopes
```

Build hello payloads with only safe fields:

```csharp
["tools"] = new JsonArray(capabilityProvider().Select(ToJson).ToArray()),
["skills"] = new JsonArray(),
```

On `session_open`, reject any non-empty `skill_ids`; reject scopes not present in `executableScopes`; call `BrokerSession.Open` with validated scopes and an empty skill set; then return `AgentMessageTypes.SessionOpened` with:

```csharp
new JsonObject
{
    ["status"] = ToolResultStatuses.Succeeded,
    ["scopes"] = new JsonArray(_session.Scopes.OrderBy(scope => scope, StringComparer.Ordinal)
        .Select(scope => (JsonNode?)scope).ToArray()),
    ["skill_ids"] = new JsonArray(),
    ["safe_summary"] = "session opened",
}
```

Remove skill package loading from `ToolExecutionService`, return an empty list from `ListSkills`, and make `ExecuteSkillAsync` return `denied/skills_disabled` as defense in depth. Remove the skill-handler constructor field from `AgentConnection`; its `skill_start` case always returns `denied/skills_disabled` after envelope/session validation. `BrokerHost` passes no skill handler. Keep source skill packages and their isolated manifest validator tests as deferred artifacts, but expose no runtime path to them.

- [ ] **Step 5: Run Broker suite and commit**

```powershell
dotnet test backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
```

Expected: all Broker tests pass; hello advertises only complete bindings, and skill execution is unreachable.

```powershell
git add -- backend-dotnet/RuAgent.Protocol/AgentToolContracts.cs backend-dotnet/RuAgent.Protocol/AgentMessageTypes.cs backend-dotnet/RuAgent.Broker/Tools/ToolBindingRegistry.cs backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs backend-dotnet/RuAgent.Broker.Tests/AgentEnvelopeTests.cs
git commit -m "feat: derive Broker capabilities from executable bindings"
```

---

### Task 4: Make Broker capabilities and verified receipts authoritative in the Public Host

**Files:**
- Modify: `backend-dotnet\RuBackend\Agent\AgentModels.cs`
- Modify: `backend-dotnet\RuBackend\Agent\IAgentBrokerClient.cs`
- Modify: `backend-dotnet\RuBackend\Agent\AgentBrokerClient.cs`
- Modify: `backend-dotnet\RuBackend\Agent\AgentSessionGrantStore.cs`
- Modify: `backend-dotnet\RuBackend\Worker\WorkerModels.cs`
- Modify: `backend-dotnet\RuBackend\Worker\WorkerEnvelope.cs`
- Modify: `backend-dotnet\RuBackend\Worker\LoopbackWorkerClient.cs`
- Modify: `backend-dotnet\RuBackend.Tests\Agent\AgentBrokerClientTests.cs`
- Modify: `backend-dotnet\RuBackend.Tests\Agent\AgentStoreTests.cs`
- Modify: `backend-dotnet\RuBackend.Tests\Agent\WorkerAgentProtocolContractTests.cs`
- Modify: `backend-dotnet\RuBackend.Tests\Worker\WorkerEnvelopeTests.cs`
- Modify: `backend-dotnet\RuBackend.Tests\Worker\LoopbackWorkerClientTests.cs`

**Interfaces:**
- Reuses the protocol-owned `AgentToolCapability` from Task 3 and adds `GetCapabilitiesAsync` to the Host-side Broker client; no second Host capability record is created.
- Removes `RunSkillAsync` from `IAgentBrokerClient` and `AgentBrokerClient`; disabled skills have no Host execution method.
- Changes `AgentToolCall` and `WorkerToolRequest` to carry the selected `CapabilityClass`; the v1 value for volume is `native_mutation`.
- Changes `AgentToolResult` to preserve private `AdapterInvoked` provenance plus `DurationMs`, `VerificationState`, `EndpointRole`, and `EndpointHash`; the Worker receipt gets all except the Host-only provenance boolean.
- Removes the caller-supplied `requiredScope` argument from `ExecuteToolAsync`; the client derives scope from a cached exact ID/version capability and the actual session grant.
- Emits `tool.receipt.delivered` only after `LoopbackWorkerClient` successfully sends `chat.tool.result` to the Worker.

- [ ] **Step 1: Write failing Broker-client capability and receipt tests**

In `AgentBrokerClientTests`, extend the fake Broker hello to include:

```csharp
["tools"] = new JsonArray
{
    new JsonObject
    {
        ["tool_id"] = "system.volume",
        ["tool_version"] = "1.0.0",
        ["scopes"] = new JsonArray("system.write"),
        ["risk"] = "reversible",
        ["availability"] = "native_pending",
        ["model_enabled"] = true,
    },
},
["skills"] = new JsonArray(),
```

Add assertions equivalent to:

```csharp
var capabilities = await client.GetCapabilitiesAsync(CancellationToken.None);
var volume = Assert.Single(capabilities);
Assert.Equal("system.volume", volume.ToolId);
Assert.Equal("1.0.0", volume.ToolVersion);
Assert.Equal(new[] { "system.write" }, volume.Scopes);
Assert.True(volume.ModelEnabled);

var grant = await client.OpenSessionAsync(
    "session-1",
    new HashSet<string>(StringComparer.Ordinal) { "system.write" },
    new HashSet<string>(StringComparer.Ordinal),
    "corr-1",
    2_000,
    CancellationToken.None);
Assert.Equal(new[] { "system.write" }, grant.Scopes);

var result = await client.ExecuteToolAsync(
    new AgentToolCall(
        "request-1", "session-1", "corr-1", "system.volume", "1.0.0",
        "native_mutation", new Dictionary<string, object?> { ["operation"] = "mute" }, 2_000),
    CancellationToken.None);
Assert.Equal("verified", result.VerificationState);
Assert.Equal("default_render_multimedia", result.EndpointRole);
Assert.Matches("^[0-9a-f]{16}$", result.EndpointHash);
Assert.Equal(12, result.DurationMs);
Assert.True(result.AdapterInvoked);
```

Make the fake `session_opened` payload deliberately return only `system.write`, even when the test requests an extra scope, and assert the grant is parsed from the response. Add rejection tests for duplicate capability IDs, an unknown risk/availability value, a missing exact version, a scope outside the actual grant, a negative/oversized duration, and a malformed endpoint hash.

The fake successful Broker receipt sets `duration_ms: 12`, `verification_state: "verified"`, and `adapter_invoked: true`. Add a policy-denied fake with `adapter_invoked: false`; reject a missing or non-boolean provenance value.

- [ ] **Step 2: Write failing Worker-wire tests**

Update the protocol contract test to construct:

```csharp
var request = new WorkerToolRequest(
    "tool-1", "session-1", "corr-1", "system.volume", "1.0.0",
    "native_mutation", new JsonObject { ["operation"] = "mute" }, 2_000);
var result = new WorkerToolResult(
    "tool-1", "succeeded", new JsonObject(), null, "volume changed",
    12, "verified", "default_render_multimedia", "0123456789abcdef");
```

In `WorkerEnvelopeTests`, require bounded non-empty `capability_class` on `chat.tool.request`; require a duration from 0 through 300,000 ms and one of `not_required`, `pending`, `verified`, or `failed` for `verification_state` on `chat.tool.result`; reject an endpoint hash that is not exactly 16 lowercase hexadecimal characters. In `LoopbackWorkerClientTests`, capture the outbound result payload and assert all five new receipt fields round-trip unchanged.

Run:

```powershell
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentBrokerClientTests|FullyQualifiedName~AgentStoreTests|FullyQualifiedName~WorkerAgentProtocolContractTests|FullyQualifiedName~WorkerEnvelopeTests|FullyQualifiedName~LoopbackWorkerClientTests"
```

Expected: compile failures for the missing capability/receipt fields and the old `ExecuteToolAsync` signature.

- [ ] **Step 3: Implement bounded Host-side models**

Keep `AgentToolCapability` in `RuAgent.Protocol` as the one shared type. Replace the receipt record in `AgentModels.cs` with:

```csharp
public sealed record AgentToolResult(
    string RequestId,
    string Status,
    JsonObject Output,
    string? ErrorCode,
    string SafeSummary,
    int DurationMs,
    string VerificationState,
    string? EndpointRole,
    string? EndpointHash,
    bool AdapterInvoked);
```

Change `AgentToolCall` to include `string CapabilityClass` immediately before `Arguments`. Add `CapabilityClass` to `WorkerToolRequest`, and append `DurationMs`, `VerificationState`, `EndpointRole`, and `EndpointHash` to `WorkerToolResult`. Keep every string bounded by the existing envelope limits; duration is 0 through 300,000 ms, endpoint role may only be null or `default_render_multimedia`, and endpoint hash may only be null or 16 lowercase hex characters.

- [ ] **Step 4: Parse, cache, and enforce capabilities in `AgentBrokerClient`**

Change the interface to:

```csharp
Task<IReadOnlyList<AgentToolCapability>> GetCapabilitiesAsync(
    CancellationToken cancellationToken);

Task<AgentToolResult> ExecuteToolAsync(
    AgentToolCall call,
    CancellationToken cancellationToken);
```

Delete `RunSkillAsync` and `RunSkillCoreAsync` from the interface/client, and remove their old success-path tests. Add a source-contract assertion that `AgentBrokerClient.cs` contains neither method name nor `skill_start`; Broker-level Task 3 tests remain the fail-closed protocol proof.

During the first authenticated round trip, parse the hello tool array into an immutable, sorted capability snapshot keyed by `(ToolId, ToolVersion)`. Accept only the four risk values and two availability values defined by Tasks 1–3; reject duplicates and unknown enum-like values. Cache actual grants by session ID only after parsing the `session_opened` response. `ExecuteToolAsync` must fail locally with `capability_unavailable` or `scope_not_granted` before writing a request if the exact binding or all declared scopes are absent. Serialize the capability class as `capability_class`; parse the complete Broker result, including duration and the literal boolean `adapter_invoked`, without inventing defaults. The local fail-closed results use `AdapterInvoked: false`.

Add `AgentSessionGrantStore.TryGet(sessionId, requiredScopes, out grant)` and require every scope with ordinal comparison; retain the one-scope overload only as a delegating compatibility seam. Cover all-scopes present, one missing, expiry, immutable grant collections, and removal in `AgentStoreTests`.

Do not echo requested scopes into `AgentSessionGrant`. Do not expose a mutable collection from either capability or grant caches. Clear both caches on disposal and clear a session grant after `CloseSessionAsync` succeeds.

- [ ] **Step 5: Serialize the complete Worker receipt and log delivery after send**

`LoopbackWorkerClient.ParseToolRequest` reads `capability_class`. Its result payload is:

```csharp
new JsonObject
{
    ["request_id"] = result.RequestId,
    ["status"] = result.Status,
    ["output"] = result.Output.DeepClone(),
    ["error_code"] = result.ErrorCode,
    ["safe_summary"] = result.SafeSummary,
    ["duration_ms"] = result.DurationMs,
    ["verification_state"] = result.VerificationState,
    ["endpoint_role"] = result.EndpointRole,
    ["endpoint_hash"] = result.EndpointHash,
};
```

Place the later Task 9 diagnostic call immediately after the awaited Worker send completes; do not emit it before or in a `finally` block. Until Task 9 adds the emitter method, keep the call behind a private no-op `ReceiptDelivered` seam covered by the ordering test.

- [ ] **Step 6: Run targeted suites and commit**

```powershell
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentBrokerClientTests|FullyQualifiedName~AgentStoreTests|FullyQualifiedName~WorkerAgentProtocolContractTests|FullyQualifiedName~WorkerEnvelopeTests|FullyQualifiedName~LoopbackWorkerClientTests"
```

Expected: all selected tests pass and no result can lose Broker verification metadata.

```powershell
git add -- backend-dotnet/RuBackend/Agent/AgentModels.cs backend-dotnet/RuBackend/Agent/IAgentBrokerClient.cs backend-dotnet/RuBackend/Agent/AgentBrokerClient.cs backend-dotnet/RuBackend/Agent/AgentSessionGrantStore.cs backend-dotnet/RuBackend/Worker/WorkerModels.cs backend-dotnet/RuBackend/Worker/WorkerEnvelope.cs backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs backend-dotnet/RuBackend.Tests/Agent/AgentBrokerClientTests.cs backend-dotnet/RuBackend.Tests/Agent/AgentStoreTests.cs backend-dotnet/RuBackend.Tests/Agent/WorkerAgentProtocolContractTests.cs backend-dotnet/RuBackend.Tests/Worker/WorkerEnvelopeTests.cs backend-dotnet/RuBackend.Tests/Worker/LoopbackWorkerClientTests.cs
git commit -m "feat: carry Broker capabilities and verified receipts"
```

---

### Task 5: Route normal chat and Agent mode through the same model-enabled binding

**Files:**
- Modify: `backend-dotnet\RuBackend\Conversation\AgentTurnCoordinator.cs`
- Modify: `backend-dotnet\RuBackend\Conversation\ConversationCoordinator.cs`
- Modify: `backend-dotnet\RuBackend\Conversation\ChatEvent.cs`
- Modify: `backend-dotnet\RuBackend\Worker\WorkerModels.cs`
- Modify: `backend-dotnet\RuBackend.Tests\Conversation\AgentTurnCoordinatorTests.cs`
- Modify: `backend-dotnet\RuBackend.Tests\Conversation\ConversationCoordinatorTests.cs`
- Modify: `backend-dotnet\RuBackend.Tests\Conversation\ChatContractFixtureTests.cs`

**Interfaces:**
- Adds a bounded `Scopes` array to `ChatEvent` for `agent.session`.
- Adds `IAgentTurnCoordinator.GetModelCapabilitiesAsync(CancellationToken)` as the only conversation-layer access to the Broker snapshot.
- Puts the safe model capability projection into `WorkerChatRequest.Settings["model_tools"]`.
- Installs the same `ToolHandler` for normal and Agent turns whenever at least one model-enabled capability exists.
- Removes `AgentTurnCoordinator.RequiredScope`; exact capability metadata determines the grant.

- [ ] **Step 1: Write failing coordinator tests for exact grants and receipts**

Replace the fake client's `LastRequiredScope` assertions with capability-driven assertions:

```csharp
Assert.Equal(new[] { "system.write" }, broker.LastRequestedScopes);
Assert.Empty(broker.LastRequestedSkillIds);
Assert.Equal("native_mutation", broker.LastCall!.CapabilityClass);
Assert.Equal("verified", result.VerificationState);
```

Make the fake capability list contain `system.volume@1.0.0` only. Add tests that assert:

1. an unknown tool ID returns `failed/capability_unavailable` without opening a session;
2. a wrong version returns the same bounded failure;
3. a capability class other than `native_mutation` returns `failed/capability_mismatch` without opening a session;
4. the emitted event sequence contains one `agent.session` event whose `Scopes` exactly equal the fake Broker's actual grant;
5. a mismatched result request ID becomes `failed/receipt_mismatch`;
6. a late result after cancellation is not yielded as completed activity.

Use this event assertion:

```csharp
var sessionEvent = Assert.Single(events, item => item.Type == "agent.session");
Assert.Equal("session-1", sessionEvent.SessionId);
Assert.Equal(new[] { "system.write" }, sessionEvent.Scopes);
```

- [ ] **Step 2: Write failing normal/Agent parity tests**

In `ConversationCoordinatorTests`, arrange a Worker fake that emits the same `WorkerToolRequest` once with `agent_mode = false` and once with `agent_mode = true`. Assert both requests receive a non-null handler and both invoke the fake Broker exactly once:

```csharp
[Theory]
[InlineData(false)]
[InlineData(true)]
public async Task Volume_action_uses_same_handler_in_both_modes(bool agentMode)
{
    var events = await RunVolumeTurnAsync(agentMode);
    Assert.Equal(1, _agent.ExecuteCount);
    Assert.Contains(events, item => item.Type == "agent.activity" && item.Status == "succeeded");
}
```

Also assert `model_tools` contains only `system.volume@1.0.0`, its exact scope, availability, and `model_enabled: true`; Broker-internal file/terminal tools must not appear. Add a zero-capability case where `model_tools` is empty and `ToolHandler` remains null.

Run:

```powershell
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentTurnCoordinatorTests|FullyQualifiedName~ConversationCoordinatorTests|FullyQualifiedName~ChatContractFixtureTests"
```

Expected: current hard-coded scopes and Agent-only handler fail the tests.

- [ ] **Step 3: Implement capability-driven session setup**

Add this interface member and implement it by delegating to the Broker client, returning an empty immutable array when no Broker is configured:

```csharp
Task<IReadOnlyList<AgentToolCapability>> GetModelCapabilitiesAsync(
    CancellationToken cancellationToken);
```

At turn setup, call that member once, filter `ModelEnabled`, and serialize only safe fields:

```csharp
request.Settings["model_tools"] = new JsonArray(modelTools.Select(tool => new JsonObject
{
    ["tool_id"] = tool.ToolId,
    ["tool_version"] = tool.ToolVersion,
    ["scopes"] = new JsonArray(tool.Scopes.OrderBy(scope => scope, StringComparer.Ordinal)
        .Select(scope => (JsonNode?)scope).ToArray()),
    ["availability"] = tool.Availability,
    ["model_enabled"] = tool.ModelEnabled,
    ["capability_class"] = tool.ToolId == "system.volume" ? "native_mutation" : "internal",
}).ToArray());
```

Install `ToolHandler` independently of `agent_mode`. In `AgentTurnCoordinator`, look up an exact ID/version in that same snapshot, open a Broker session with exactly its scopes and no skill IDs, store the actual returned grant, and publish `new ChatEvent("agent.session", SessionId: grant.SessionId, Scopes: grant.Scopes.OrderBy(scope => scope, StringComparer.Ordinal).ToArray())` once per newly opened session.

Delete the `RequiredScope` switch and every broad hard-coded scope set. Derive the canonical capability class from the exact model projection (`system.volume` maps to `native_mutation`) and reject a Worker mismatch before session open. Construct `AgentToolCall` from the validated Worker request and let `AgentBrokerClient` perform its second grant check.

Map the Broker receipt to `WorkerToolResult` by copying request ID, outer status, bounded output clone, error code, safe summary, duration, verification state, endpoint role, and endpoint hash exactly. Do not forward the Host-only `AdapterInvoked` provenance field. Local Host failures set duration `0`, verification `failed` for a rejected mutation, and no endpoint proof.

- [ ] **Step 4: Make activity terminal and bounded**

Change activity construction so `Content` never contains output or prose. The request event carries `Status: "requested"`; the terminal event carries only `Status`, `ToolId`, `ErrorCode`, and `SessionId`. Preserve `approval.required` as a separate event. Do not publish a completed event for a stale or canceled receipt. Remove the generic `ChatToolRequest` activity write in `ConversationCoordinator`: `AgentTurnCoordinator` is the sole source of requested/terminal action activity, so each handled action produces exactly one of each rather than a duplicate content-only event.

Add `Scopes` to `ChatEvent` as:

```csharp
[property: JsonPropertyName("scopes")]
[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
IReadOnlyList<string>? Scopes = null
```

Reject more than 16 scopes, an empty scope, or a scope longer than 64 characters before creating the event.

- [ ] **Step 5: Run conversation suites and commit**

```powershell
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentTurnCoordinatorTests|FullyQualifiedName~ConversationCoordinatorTests|FullyQualifiedName~ChatContractFixtureTests|FullyQualifiedName~ChatWebSocketEndpointTests"
```

Expected: both modes use the one capability-driven path and public chat JSON remains backward-compatible apart from the additive `agent.session.scopes` field.

```powershell
git add -- backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs backend-dotnet/RuBackend/Conversation/ChatEvent.cs backend-dotnet/RuBackend/Worker/WorkerModels.cs backend-dotnet/RuBackend.Tests/Conversation/AgentTurnCoordinatorTests.cs backend-dotnet/RuBackend.Tests/Conversation/ConversationCoordinatorTests.cs backend-dotnet/RuBackend.Tests/Conversation/ChatContractFixtureTests.cs
git commit -m "feat: route chat actions through actual Broker grants"
```

---

### Task 6: Add a bounded Worker action loop with truthful receipt rendering

**Files:**
- Create: `src\ru_runtime\worker\v1\action_loop.py`
- Modify: `src\ru_runtime\worker\v1\handlers.py`
- Modify: `src\ru_runtime\worker\v1\protocol.py`
- Modify: `src\ru_runtime\generation\runner.py`
- Modify: `src\ru_runtime\policy\style.py`
- Create: `tests\test_worker_action_loop.py`
- Modify: `tests\test_worker_agent_protocol.py`
- Modify: `tests\test_worker_runtime_handlers.py`
- Modify: `tests\test_runner_status.py`

**Interfaces:**
- `parse_model_capabilities(settings)` returns an immutable tuple of `ModelCapability` values and accepts only the safe Host projection.
- `detect_volume_intent(prompt) -> bool` is a bounded Vietnamese/English routing hint, never execution proof.
- `validate_selection(text, capabilities) -> ActionSelection | None` accepts one exact advertised call and rejects any other tool/version.
- `validate_receipt(selection, payload) -> ActionReceipt` requires matching request/session/correlation identifiers and verified mutation success.
- `failure_reply(error_code) -> str` returns deterministic Vietnamese copy without tool arguments or native detail.
- `handlers.chat` executes at most one selection/result cycle and never treats a tool failure as `generation_failed`.

- [ ] **Step 1: Write failing pure action-loop tests**

Create `test_worker_action_loop.py` with table-driven coverage:

```python
@pytest.mark.parametrize(
    "prompt",
    (
        "Hạ âm lượng xuống 20%",
        "Tăng tiếng lên một chút",
        "Tắt tiếng giúp anh",
        "Set the volume to 30 percent",
        "Please unmute my computer",
    ),
)
def test_detects_explicit_volume_intent(prompt: str) -> None:
    assert detect_volume_intent(prompt) is True


@pytest.mark.parametrize(
    "prompt",
    (
        "Nói nhỏ hơn một chút",
        "Bài hát này có âm lượng cảm xúc lớn",
        "Giải thích volume trong Docker",
        "Anh nghe chưa rõ câu trả lời",
    ),
)
def test_does_not_route_ambiguous_prose(prompt: str) -> None:
    assert detect_volume_intent(prompt) is False
```

Use a `settings` fixture with exactly one `system.volume@1.0.0` capability. Assert malformed arrays, duplicate IDs, extra fields, more than 16 capabilities, unknown availability, absent `native_mutation`, and a non-model-enabled tool fail closed with `ActionProtocolError("invalid_model_tools")`.

Add exact selection assertions:

```python
selection = validate_selection(
    '{"tool_id":"system.volume","tool_version":"1.0.0",'
    '"arguments":{"operation":"set","level":20}}',
    capabilities,
)
assert selection.tool_id == "system.volume"
assert selection.capability_class == "native_mutation"

with pytest.raises(ActionProtocolError, match="capability_unavailable"):
    validate_selection(
        '{"tool_id":"app.open","tool_version":"1.0.0",'
        '"arguments":{"target":"browser"}}',
        capabilities,
    )
```

Receipt tests must prove all of these:

- `succeeded + verified` is accepted for a mutating volume call;
- `succeeded + pending`, `succeeded + failed`, or missing verification becomes `receipt_unverified`;
- `failed + failed` is a valid terminal failure and preserves only its safe error code;
- wrong request/session/correlation IDs become `receipt_mismatch`;
- an expired deadline becomes `receipt_late`;
- endpoint role/hash are validated but never interpolated into user copy.

The canonical success fixture contains `duration_ms: 12`, `verification_state: "verified"`, `endpoint_role: "default_render_multimedia"`, and a 16-character lowercase endpoint hash. No test may infer `verified` from the legacy `output.verified` field.

- [ ] **Step 2: Write failing handler parity and no-false-success tests**

Parameterize the existing volume loop test over `agent_mode`:

```python
@pytest.mark.parametrize("agent_mode", (False, True))
def test_volume_request_uses_selection_then_verified_receipt(agent_mode: bool) -> None:
    runner = SequencedRunner(
        '{"tool_id":"system.volume","tool_version":"1.0.0",'
        '"arguments":{"operation":"set","level":2}}',
        "Đã đặt âm lượng ở mức 2% cho anh.",
    )
    replies = asyncio.run(_run_action_turn(runner, verified_result(), agent_mode))
    assert [call["structured_tools_only"] for call in runner.calls] == [True, False]
    assert replies[-1].type == CHAT_DONE
```

Add these regressions:

1. normal chat prompt `Hạ âm lượng xuống 2%` plus model prose `Đã chỉnh xong` emits no `CHAT_TOOL_REQUEST` and ends with deterministic `Không thể thực hiện thay đổi âm lượng.`;
2. Agent mode does the same for the detected volume prompt;
3. ordinary normal chat never enters selection and still streams once;
4. ordinary Agent chat may return plain prose from its selection phase and yields that prose without a second generation;
5. `succeeded` with `verification_state != verified` never invokes follow-up generation;
6. stale, mismatched, and late receipts emit the bounded action failure, not `_error("generation_failed")`;
7. two tool-call objects in one model response are rejected and no request is emitted;
8. a capability-absent volume request fails deterministically without calling a handler.

Update `_agent_payload` into `_action_payload(agent_mode)` and include:

```python
"settings": {
    "agent_mode": agent_mode,
    "allow_web_search": False,
    "model_tools": [{
        "tool_id": "system.volume",
        "tool_version": "1.0.0",
        "scopes": ["system.write"],
        "availability": "native_pending",
        "model_enabled": True,
        "capability_class": "native_mutation",
    }],
},
```

Run:

```powershell
& $ruPython -m pytest -q tests/test_worker_action_loop.py tests/test_worker_agent_protocol.py tests/test_worker_runtime_handlers.py tests/test_runner_status.py
```

Expected: import and parity failures because normal chat currently bypasses the queue and verified receipt fields are not validated.

- [ ] **Step 3: Implement immutable action-loop value objects and validators**

Use frozen slotted dataclasses:

```python
@dataclass(frozen=True, slots=True)
class ModelCapability:
    tool_id: str
    tool_version: str
    scopes: tuple[str, ...]
    availability: Literal["executable", "native_pending"]
    model_enabled: bool
    capability_class: Literal["native_mutation"]


@dataclass(frozen=True, slots=True)
class ActionSelection:
    tool_id: str
    tool_version: str
    capability_class: str
    arguments: dict[str, object]


@dataclass(frozen=True, slots=True)
class ActionReceipt:
    status: str
    error_code: str | None
    safe_summary: str
    duration_ms: int
    verification_state: str
```

Bounds are: at most 16 capabilities; IDs/version/class/availability/scopes at most 128/32/64/32/64 characters; at most 16 scopes per tool; duration from 0 through 300,000 ms; serialized arguments and output at most the existing protocol constants. Copy dictionaries before storing them. Reject any item in `model_tools` whose `model_enabled` is not the literal boolean `true`; the Host must omit internal bindings instead of asking the Worker to ignore them.

The detector must normalize case and whitespace, then require a device-audio noun (`âm lượng`, `tiếng máy`, `loa`, `volume`, `sound`, `mute`, or `unmute`) and a mutation/query verb (`tăng`, `giảm`, `hạ`, `đặt`, `tắt`, `bật`, `increase`, `decrease`, `set`, `turn`, `mute`, or `unmute`). Explicit `mute`/`unmute` satisfies both groups. Keep detector constants in this module and cover every term with tests.

In `style.py`, replace the static Agent tool prompt with `ru_action_selection_prompt(capabilities)`. It accepts the already validated sequence, returns the exact `system.volume@1.0.0` operation/range instructions only when that capability is present, permits ordinary prose when no action is needed, and raises `ValueError("unsupported_model_capability")` for every other ID/version. `runner.generate_reply` gains a keyword-only `action_capabilities=()` parameter and uses this function only when `structured_tools_only` is true. Add runner tests proving no static capability text appears in ordinary/follow-up prompts and an empty selection list never advertises volume.

- [ ] **Step 4: Integrate one selection cycle into `handlers.chat`**

Add `model_tools: tuple[ModelCapability, ...]` to the private `_ChatRequest` and populate it by calling `parse_model_capabilities(settings)` inside `_parse_chat_request`. Replace the `agent_mode` gate with:

```python
capabilities = request.model_tools
volume_intent = detect_volume_intent(request.prompt)
volume_capability = next(
    (item for item in capabilities if item.tool_id == "system.volume"),
    None,
)
if volume_intent and volume_capability is None:
    raise ActionProtocolError("capability_unavailable")
selection_required = (
    isinstance(tool_result_queue, asyncio.Queue)
    and bool(capabilities)
    and (request.agent_mode or volume_intent)
)
```

For normal chat without an explicit volume intent, retain the existing streamed generation. For either mode with a required selection, call the model non-visibly with `structured_tools_only=True`. A valid exact selection emits one `CHAT_TOOL_REQUEST` including `capability_class`; then await one receipt until the request deadline.

Extend the internal runner call with `action_capabilities=capabilities if phase == "tool_request" else ()`. The runner passes only the validated exact ID/version and argument bounds into `ru_action_selection_prompt`; it does not import Worker action-loop types or receive scopes, risk, endpoint metadata, arguments, or receipts. This prevents a static prompt from advertising volume when the Host supplied no model capability.

If an Agent selection phase returns ordinary prose and no explicit volume intent was detected, treat it as the visible reply and stop. For a detected volume request, missing/malformed/unavailable selection returns deterministic failure. Once a request is emitted, every timeout, mismatch, cancellation, failure, or unverified receipt ends the action loop without model follow-up. Only a validated verified success may enter `agent_follow_up=True`.

Catch `ActionProtocolError` separately from generation exceptions:

```python
except ActionProtocolError as error:
    publish(WorkerReply(CHAT_DONE, {"raw_reply": failure_reply(error.code)}))
```

Do not place receipt output into a prompt. The follow-up turn contains only `status`, `safe_summary`, `error_code`, `duration_ms`, and `verification_state`; omit output values and endpoint proof fields.

- [ ] **Step 5: Extend private Worker protocol validation**

Require `capability_class` on tool requests. Require bounded `duration_ms` and `verification_state` on results with the four allowed values. Validate optional role/hash exactly as in Task 4. Reject unknown result fields except the existing context fields and the new receipt fields. Preserve protocol version 1 because these fields are inside the private additive envelope and both endpoints ship together.

- [ ] **Step 6: Run targeted Python tests and commit**

```powershell
& $ruPython -m pytest -q tests/test_worker_action_loop.py tests/test_worker_agent_protocol.py tests/test_worker_runtime_handlers.py tests/test_worker_protocol.py tests/test_worker_server.py tests/test_runner_status.py
```

Expected: all selected tests pass; verified success is the only path to a success follow-up in both modes.

```powershell
git add -- src/ru_runtime/worker/v1/action_loop.py src/ru_runtime/worker/v1/handlers.py src/ru_runtime/worker/v1/protocol.py src/ru_runtime/generation/runner.py src/ru_runtime/policy/style.py tests/test_worker_action_loop.py tests/test_worker_agent_protocol.py tests/test_worker_runtime_handlers.py tests/test_runner_status.py
git commit -m "feat: add truthful Worker action loop"
```

---

### Task 7: Retire Python desktop side effects and keep marker compatibility non-executable

**Files:**
- Delete: `src\ru_runtime\system\volume.py`
- Modify: `src\ru_runtime\system\pc_tools.py`
- Modify: `src\ru_runtime\generation\tool_calls.py`
- Modify: `src\ru_runtime\generation\orchestration.py`
- Modify: `src\ru_runtime\generation\runner.py`
- Modify: `src\ru_runtime\policy\style.py`
- Modify: `pyproject.toml`
- Delete: `tests\test_volume_control.py`
- Modify: `tests\test_system_pc_tools.py`
- Modify: `tests\test_tool_calls.py`
- Modify: `tests\test_reply_orchestration.py`
- Modify: `tests\test_ru_style.py`
- Modify: `tests\test_python_package_architecture.py`

**Interfaces:**
- `parse_legacy_volume_intent(text) -> ToolCall | None` translates one legacy volume marker into the same structured intent; it performs no action.
- `execute_reply` strips private app/volume markers and performs no process launch or Core Audio call.
- Python package dependencies no longer include `pycaw` or `comtypes`.
- `pc_tools.py` contains no application-launch or volume-mutation function.

- [ ] **Step 1: Replace native Python tests with failing ownership tests**

Delete `test_volume_control.py`. In `test_python_package_architecture.py`, add:

```python
def test_python_runtime_has_no_native_volume_owner() -> None:
    assert not (ROOT / "src/ru_runtime/system/volume.py").exists()
    pyproject = (ROOT / "pyproject.toml").read_text(encoding="utf-8")
    assert "pycaw" not in pyproject.lower()
    assert "comtypes" not in pyproject.lower()
    sources = "\n".join(
        path.read_text(encoding="utf-8")
        for path in (ROOT / "src/ru_runtime").rglob("*.py")
    )
    assert "adjust_system_volume" not in sources
    assert "open_application_or_url" not in sources
```

Rewrite `test_reply_orchestration.py` to patch `os.startfile`, `subprocess.Popen`, and `ctypes.windll` with functions that fail the test if called. Assert:

```python
result = execute_reply(
    "[APP: browser] [VOLUME: set 2] Em đã làm xong.",
    allow_legacy_markers=True,
)
assert result == "Em đã làm xong."
```

Run:

```powershell
& $ruPython -m pytest -q tests/test_python_package_architecture.py tests/test_reply_orchestration.py
```

Expected: the current module, dependencies, and direct execution paths fail ownership assertions.

- [ ] **Step 2: Write failing legacy-marker translation tests**

In `test_tool_calls.py`, add:

```python
@pytest.mark.parametrize(
    ("marker", "arguments"),
    (
        ("[VOLUME: get]", {"operation": "get"}),
        ("[VOLUME: set 2]", {"operation": "set", "level": 2}),
        ("[VOLUME: increase 5]", {"operation": "increase", "amount": 5}),
        ("[VOLUME: decrease 5]", {"operation": "decrease", "amount": 5}),
        ("[VOLUME: mute]", {"operation": "mute"}),
        ("[VOLUME: unmute]", {"operation": "unmute"}),
        ("[VOLUME: up]", {"operation": "increase", "amount": 4}),
        ("[VOLUME: down]", {"operation": "decrease", "amount": 4}),
    ),
)
def test_legacy_volume_marker_translates_to_intent(marker: str, arguments: dict) -> None:
    call = parse_legacy_volume_intent(marker)
    assert call == ToolCall("system.volume", "1.0.0", arguments)
```

Also assert `parse_legacy_volume_intent("[VOLUME: set 2] Em đã chỉnh xong.")` returns only the structured `ToolCall`; the prose is neither returned nor rendered.

Accept one legacy marker surrounded by ordinary prose but discard all surrounding prose; this preserves old model output without preserving its success claim. Reject multiple volume markers, any accompanying app/search/VTS marker, unknown operations, booleans, `set` outside 0–100, and relative amounts outside 1–50. In the Task 6 action loop, try strict JSON first and this compatibility parser second; both return an intent that still requires Broker execution and a verified receipt.

- [ ] **Step 3: Remove direct app and volume execution**

Delete the Core Audio module and both `adjust_system_volume` and `open_application_or_url` from `pc_tools.py`. Remove their unit tests and every import. In `orchestration.execute_reply`, keep only deterministic marker stripping and whitespace cleanup. It must not import `ru_runtime.system`, `os`, `subprocess`, `webbrowser`, `ctypes`, `pycaw`, or `comtypes`.

Remove marker instructions from the normal style prompt. The structured selection prompt may describe only the capabilities supplied in `model_tools`; it must explicitly say that prose is not execution and success can be stated only after a verified receipt. `runner.py` must preserve raw marker text only for the non-visible selection return so `parse_legacy_volume_intent` can translate it; visible normal/follow-up output always strips markers.

Remove the two Windows dependencies from `pyproject.toml`. Regenerate the lock file only if this repository tracks one and the dependency command changes it; otherwise do not create a new lock artifact.

- [ ] **Step 4: Run ownership and generation suites**

```powershell
& $ruPython -m pytest -q tests/test_python_package_architecture.py tests/test_system_pc_tools.py tests/test_tool_calls.py tests/test_reply_orchestration.py tests/test_ru_style.py tests/test_worker_action_loop.py tests/test_worker_runtime_handlers.py
```

Expected: all selected tests pass and repository search finds no Python native action owner:

```powershell
rg -n "pycaw|comtypes|adjust_system_volume|open_application_or_url|ru_runtime\.system\.volume" src tests pyproject.toml
```

Expected: no matches.

- [ ] **Step 5: Commit the ownership cutover**

```powershell
git add -- pyproject.toml src/ru_runtime/system/pc_tools.py src/ru_runtime/generation/tool_calls.py src/ru_runtime/generation/orchestration.py src/ru_runtime/generation/runner.py src/ru_runtime/policy/style.py src/ru_runtime/worker/v1/action_loop.py tests/test_system_pc_tools.py tests/test_tool_calls.py tests/test_reply_orchestration.py tests/test_ru_style.py tests/test_python_package_architecture.py
git add -u -- src/ru_runtime/system/volume.py tests/test_volume_control.py
git commit -m "refactor: make Broker the sole desktop action owner"
```

---

### Task 8: Render the actual session grant in the existing GUI surface

**Files:**
- Create: `gui-winui3\Services\AgentSessionGrantModel.cs`
- Modify: `gui-winui3\Services\AgentSessionService.cs`
- Modify: `gui-winui3\Services\ChatWebSocketClient.cs`
- Modify: `gui-winui3\Views\ChatView.xaml.cs`
- Modify: `gui-winui3.Tests\AgentSessionContractTests.cs`
- Create: `gui-winui3.Tests\AgentSessionGrantModelTests.cs`
- Create: `gui-winui3.Tests\AgentSessionUiSourceContractTests.cs`

**Interfaces:**
- `AgentSessionGrantModel.TryParse(JsonElement, out AgentSessionGrantModel)` validates one `agent.session` frame.
- `AgentSessionService.ApplyGrant(AgentSessionGrantModel)` replaces hard-coded local scopes with the actual Host grant.
- `ChatWebSocketClient.OnAgentSessionReceived` carries the bounded model to the view.
- `ChatView.xaml.cs` uses the existing `AgentActivity` text surface; no XAML or new visual component is added.

- [ ] **Step 1: Write failing bounded-grant model tests**

Create tests for this accepted frame:

```csharp
using var document = JsonDocument.Parse("""
{
  "type": "agent.session",
  "session_id": "session-1",
  "scopes": ["system.write"]
}
""");

Assert.True(AgentSessionGrantModel.TryParse(document.RootElement, out var grant));
Assert.Equal("session-1", grant.SessionId);
Assert.Equal(new[] { "system.write" }, grant.Scopes);
```

Reject missing/blank session IDs, a non-array `scopes`, duplicates, empty values, more than 16 values, a value longer than 64 characters, an object element, and unknown top-level fields other than the public `ChatEvent` fields. Verify that the returned scope collection is sorted, deduplicated only by rejecting duplicates, and cannot be cast to a mutable list.

Change `AgentSessionContractTests` to assert:

```csharp
var service = new AgentSessionService();
service.ObserveSession("session-1");
service.BeginRequest("session-1");
Assert.False(service.HasActualGrant);
Assert.Empty(service.Scopes);

service.ApplyGrant(new AgentSessionGrantModel("session-1", new[] { "system.write" }));
Assert.True(service.HasActualGrant);
Assert.Equal(new[] { "system.write" }, service.Scopes);
```

Also prove that `ObserveSession("session-1")` followed by an actual grant works even when Agent mode was never enabled, so normal-chat actions display truthfully. A grant for a different active session is rejected. `Disable` clears mode/pending/grant state but keeps the observed conversation session eligible for a later normal-chat grant; observing a new session clears every old grant.

- [ ] **Step 2: Write the failing source contract without touching XAML**

`AgentSessionUiSourceContractTests` reads `ChatView.xaml.cs` and `ChatView.xaml` as text and asserts:

```csharp
Assert.Contains("OnAgentSessionReceived", codeBehind);
Assert.Contains("Quyền đã cấp: {string.Join(\", \", grant.Scopes)}", codeBehind);
Assert.DoesNotContain("workspace and terminal tools", codeBehind);
Assert.DoesNotContain("_scopes.Add(\"workspace.read\")", serviceSource);
Assert.Contains("x:Name=\"AgentActivity\"", xaml);
```

The test must not assert formatting, colors, spacing, or any content from unrelated UI regions.

Run:

```powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentSessionContractTests|FullyQualifiedName~AgentSessionGrantModelTests|FullyQualifiedName~AgentSessionUiSourceContractTests"
```

Expected: missing model/event and the current hard-coded three-scope copy fail.

- [ ] **Step 3: Implement actual-grant parsing and state**

Use an immutable record with a private copied array:

```csharp
public sealed record AgentSessionGrantModel
{
    public string SessionId { get; }
    public IReadOnlyList<string> Scopes { get; }

    public AgentSessionGrantModel(string sessionId, IEnumerable<string> scopes)
    {
        SessionId = ValidateSessionId(sessionId);
        Scopes = Array.AsReadOnly(ValidateScopes(scopes));
    }

}
```

Add `public static bool TryParse(JsonElement root, out AgentSessionGrantModel grant)` to the record. Its implementation first sets `grant` to a private empty sentinel, validates the root object and exact bounded fields, constructs the immutable model on success, and returns false on `JsonException`, `InvalidOperationException`, or argument validation failure.

Implement the body as a strict parser, not a serializer round-trip. `AgentSessionService.ObserveSession` stores the current public conversation session and clears an old grant on change. `BeginRequest` sets only mode/pending state and an empty grant. `ApplyGrant` requires the observed session ID, replaces its observable scope collection, then sets `HasActualGrant = true`; it accepts actual grants from either normal chat or Agent mode. `IsEnabled` remains the user's mode-toggle state and must not imply a grant exists.

- [ ] **Step 4: Wire the event and existing activity text**

Add `event Action<AgentSessionGrantModel>? OnAgentSessionReceived` to `ChatWebSocketClient`; parse `agent.session` before `agent.activity`. Malformed grant frames are ignored without changing prior state.

In `ChatView.xaml.cs`:

- subscribe/unsubscribe the new event alongside the existing Agent events;
- call `_agentSession.ObserveSession(sessionId)` from `OnWsSessionReceived` before clearing mode state;
- replace `_agentSession.Enable` with `_agentSession.BeginRequest` when the toggle is enabled;
- show `Đang chờ Host cấp quyền…` before an actual grant;
- on a matching grant, call `ApplyGrant` and set existing `AgentActivity.Text` to `Quyền đã cấp: system.write` using the actual sorted values;
- clear the grant on session change, disconnect, or mode disable;
- do not infer or append workspace, terminal, Web, mail, calendar, task, or skill scopes.

Do not edit `ChatView.xaml`; the existing `AgentActivity` element is sufficient and the primary checkout's XAML is dirty.

- [ ] **Step 5: Run GUI tests and commit**

```powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentSessionContractTests|FullyQualifiedName~AgentSessionGrantModelTests|FullyQualifiedName~AgentSessionUiSourceContractTests|FullyQualifiedName~AgentApprovalTests"
```

Expected: selected GUI contract tests pass and the XAML file remains byte-for-byte untouched in the worktree.

```powershell
git diff --exit-code -- gui-winui3/Views/ChatView.xaml
git add -- gui-winui3/Services/AgentSessionGrantModel.cs gui-winui3/Services/AgentSessionService.cs gui-winui3/Services/ChatWebSocketClient.cs gui-winui3/Views/ChatView.xaml.cs gui-winui3.Tests/AgentSessionContractTests.cs gui-winui3.Tests/AgentSessionGrantModelTests.cs gui-winui3.Tests/AgentSessionUiSourceContractTests.cs
git commit -m "feat: show actual Agent session grants"
```

---

### Task 9: Add a correlated privacy-safe action receipt trail

**Files:**
- Modify: `backend-dotnet\RuBackend\Diagnostics\DiagnosticEvent.cs`
- Modify: `backend-dotnet\RuBackend\Diagnostics\DiagnosticEventPolicy.cs`
- Modify: `backend-dotnet\RuBackend\Diagnostics\DiagnosticCodes.cs`
- Modify: `backend-dotnet\RuBackend\Diagnostics\IDiagnosticEmitter.cs`
- Modify: `backend-dotnet\RuBackend\Diagnostics\DiagnosticEmitter.cs`
- Modify: `backend-dotnet\RuBackend\Conversation\AgentTurnCoordinator.cs`
- Modify: `backend-dotnet\RuBackend\Worker\LoopbackWorkerClient.cs`
- Modify: `src\ru_runtime\persistence\diagnostic_schema.py`
- Modify: `src\ru_runtime\worker\v1\handlers.py`
- Modify: `backend-dotnet\RuBackend.Tests\Diagnostics\DiagnosticEventTests.cs`
- Modify: `backend-dotnet\RuBackend.Tests\Diagnostics\DiagnosticEmitterTests.cs`
- Modify: `backend-dotnet\RuBackend.Tests\Conversation\AgentTurnCoordinatorTests.cs`
- Modify: `backend-dotnet\RuBackend.Tests\Worker\LoopbackWorkerClientTests.cs`
- Modify: `tests\test_diagnostic_schema.py`
- Modify: `tests\test_privacy_logging.py`
- Modify: `tests\test_worker_runtime_handlers.py`

**Interfaces:**
- Adds optional `ToolId`, `ToolVersion`, `VerificationState`, `EndpointRole`, and `EndpointHash` to the shared diagnostic shape.
- Adds exact event codes: `tool.requested`, `tool.policy.denied`, `tool.approval.required`, `tool.adapter.started`, `tool.adapter.completed`, `tool.verification.succeeded`, `tool.verification.failed`, `tool.receipt.delivered`, and `tool.receipt.rejected`.
- All action events share one correlation ID and contain no arguments, outputs, values, prompts, replies, paths, scripts, or native error text.

- [ ] **Step 1: Write failing diagnostic schema and privacy tests**

Extend C# and Python serialization tests with this safe event:

```csharp
var evt = new DiagnosticEvent(
    EventId: "evt_action_1",
    Ts: 1720000000.0,
    Iso: "2026-08-22T00:00:00.000Z",
    Level: "info",
    Component: "host.agent",
    EventCode: DiagnosticCodes.ToolVerificationSucceeded,
    CorrelationId: "c_abcdef1234567890",
    DurationMs: 12.0,
    Status: "succeeded",
    ToolId: "system.volume",
    ToolVersion: "1.0.0",
    VerificationState: "verified",
    EndpointRole: "default_render_multimedia",
    EndpointHash: "0123456789abcdef");
```

Assert the serialized key set is exactly the existing allowlist plus those five fields. Add sanitizer cases for overlong/invalid tool IDs, invalid semver, unknown verification states, any endpoint role except `default_render_multimedia`, uppercase/non-hex/wrong-length hashes, and values containing `Bearer`, a path, URL, whitespace, or punctuation outside the safe pattern. Invalid optional values must become null; they must never be copied or generalized.

In both privacy suites, try to inject `arguments`, `output`, `level`, `muted`, `prompt`, `reply`, `path`, `script`, `stdout`, `stderr`, `device_name`, and `exception_message`. Assert none survives serialization. Do not add any of those fields to the record. Add `denied`, `approval_required`, and `timed_out` to the existing diagnostic status allowlist so every outer action status remains distinguishable; keep existing `timeout` for older non-action events.

- [ ] **Step 2: Write failing correlated-sequence tests**

Use a recording emitter in `AgentTurnCoordinatorTests` and assert a verified volume call whose receipt has `AdapterInvoked: true` produces this ordered subsequence with one correlation ID:

```text
tool.requested
tool.adapter.started
tool.adapter.completed
tool.verification.succeeded
```

Assert failed readback ends in `tool.verification.failed`; a policy denial or approval-required receipt with `AdapterInvoked: false` emits no adapter-start event. Approval-required emits its own code and no completion until replay. Reject the impossible combinations `AdapterInvoked: false` with verified success and `AdapterInvoked: true` with a pre-dispatch policy code. In `LoopbackWorkerClientTests`, use a fake connection whose result send blocks: assert `tool.receipt.delivered` is absent while blocked and appears once only after send returns. A throwing send must never emit delivery.

In `test_worker_runtime_handlers.py`, feed a mismatched receipt and assert one `tool.receipt.rejected` event with `error_code=receipt_mismatch`, the expected tool ID/version, and no output values.

Run:

```powershell
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~DiagnosticEventTests|FullyQualifiedName~DiagnosticEmitterTests|FullyQualifiedName~AgentTurnCoordinatorTests|FullyQualifiedName~LoopbackWorkerClientTests"
& $ruPython -m pytest -q tests/test_diagnostic_schema.py tests/test_privacy_logging.py tests/test_worker_runtime_handlers.py
```

Expected: missing fields/codes and missing emission points fail.

- [ ] **Step 3: Extend both bounded diagnostic schemas**

Append these optional record fields in C# and the equivalent optional dataclass fields in Python:

```csharp
string? ToolId = null,
string? ToolVersion = null,
string? VerificationState = null,
string? EndpointRole = null,
string? EndpointHash = null
```

Allow verification states `not_required`, `pending`, `verified`, and `failed`. Use anchored patterns `^[a-z0-9_.-]{1,128}$`, semantic version `^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$`, and endpoint hash `^[0-9a-f]{16}$`. Keep the endpoint role exact. Extend serialization/deserialization explicitly; do not enable arbitrary extension data.

Add optional parameters with the same names to `IDiagnosticEmitter.TryEmit` and `DiagnosticEmitter.TryEmit`, after `exceptionKind`, preserving all existing call sites through defaults.

- [ ] **Step 4: Emit the action lifecycle at ownership boundaries**

`AgentTurnCoordinator` emits requested before dispatch. After the authoritative Broker receipt arrives, it uses `AdapterInvoked` to append the truthful logical trail: false produces only policy-denied or approval-required; true produces adapter-started, adapter-completed from the outer status/duration, then exactly one verification event. Do not guess adapter start on transport failure or a missing receipt. Use the Worker-provided correlation ID after validation; generate a bounded correlation ID only when absent. A verified event is legal only for outer `succeeded`; a failed verification event is emitted for `verification_state: failed`; `pending` is emitted only with approval-required and never as adapter success. The Host-only `AdapterInvoked` field is not serialized to the Worker or public chat.

`LoopbackWorkerClient` replaces the temporary Task 4 no-op seam with `tool.receipt.delivered` after the awaited result send. Worker receipt validation catches `ActionProtocolError`, writes `tool.receipt.rejected` through `get_diagnostic_log_writer()`, and includes only code/correlation/tool ID/version/status. Diagnostic failure must never alter the user-visible result.

- [ ] **Step 5: Run diagnostic and privacy suites and commit**

```powershell
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~Diagnostic"
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentTurnCoordinatorTests|FullyQualifiedName~LoopbackWorkerClientTests"
& $ruPython -m pytest -q tests/test_diagnostic_schema.py tests/test_diagnostic_log.py tests/test_privacy_logging.py tests/test_worker_runtime_handlers.py
```

Expected: all selected suites pass and diagnostics expose proof state but no private content.

```powershell
git add -- backend-dotnet/RuBackend/Diagnostics/DiagnosticEvent.cs backend-dotnet/RuBackend/Diagnostics/DiagnosticEventPolicy.cs backend-dotnet/RuBackend/Diagnostics/DiagnosticCodes.cs backend-dotnet/RuBackend/Diagnostics/IDiagnosticEmitter.cs backend-dotnet/RuBackend/Diagnostics/DiagnosticEmitter.cs backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs backend-dotnet/RuBackend/Worker/LoopbackWorkerClient.cs src/ru_runtime/persistence/diagnostic_schema.py src/ru_runtime/worker/v1/handlers.py backend-dotnet/RuBackend.Tests/Diagnostics/DiagnosticEventTests.cs backend-dotnet/RuBackend.Tests/Diagnostics/DiagnosticEmitterTests.cs backend-dotnet/RuBackend.Tests/Conversation/AgentTurnCoordinatorTests.cs backend-dotnet/RuBackend.Tests/Worker/LoopbackWorkerClientTests.cs tests/test_diagnostic_schema.py tests/test_privacy_logging.py tests/test_worker_runtime_handlers.py
git commit -m "feat: log bounded action receipt trails"
```

---

### Task 10: Separate contract, adapter, and opt-in native acceptance evidence

**Files:**
- Modify: `scripts\harness\artifacts.py`
- Modify: `scripts\harness\deterministic_worker.py`
- Modify: `scripts\harness\session.py`
- Modify: `scripts\aspnetcore_compatibility_harness.py`
- Modify: `gui-winui3.Harness\Program.cs`
- Modify: `gui-winui3.Harness\UiAutomationScenario.cs`
- Create: `scripts\native_volume_acceptance.py`
- Create: `backend-dotnet\RuAgent.Broker.Tests\AdapterConformanceTests.cs`
- Modify: `tests\test_harness_artifacts.py`
- Modify: `tests\test_harness_session.py`
- Modify: `tests\test_agent_harness.py`
- Create: `tests\test_native_volume_acceptance.py`

**Interfaces:**
- the required keyword-only `evidence_class` parameter on `build_artifact` accepts exactly `contract`, `adapter`, or `native` while retaining Harness schema version 2.
- `agent_tool_loop_completed(events, expected_status)` requires one terminal tool activity with the exact expected outer status plus chat completion.
- Deterministic Harness workspace effects are confined to a fresh temporary fixture and removed during cleanup.
- Native acceptance requires `--confirm-native-volume-change`, launches the published Broker executable as a child process, verifies one one-point change, and restores the original state in `finally`.

- [ ] **Step 1: Write failing evidence-class and terminal-status tests**

Update every `build_artifact` test call with `evidence_class="contract"`, then assert:

```python
assert artifact["schema_version"] == 2
assert artifact["evidence_class"] == "contract"
```

Parameterize invalid values `"deterministic"`, `"live"`, `""`, `None`, and `1`; each must raise `ValueError("invalid evidence class")`.

Replace the loose event predicate tests with:

```python
events = [
    {"type": "agent.activity", "tool_id": "system.volume", "status": "requested"},
    {"type": "agent.activity", "tool_id": "system.volume", "status": "failed",
     "error_code": "invalid_arguments"},
    {"type": "done"},
]
assert agent_tool_loop_completed(events, expected_status="failed")
assert not agent_tool_loop_completed(events, expected_status="succeeded")
```

Add failures for missing terminal activity, an unknown status, content-only activity, completion before the terminal activity, two terminal results, and a mismatched tool ID. Approval-required/result/replay assertions move into a separate `broker_approval_lifecycle_completed` predicate and do not make the tool-loop predicate pass.

- [ ] **Step 2: Write failing deterministic ownership tests**

In `test_agent_harness.py`, replace source assertions for `web.search` with:

```python
assert '"tool_id": "system.volume"' in worker_source
assert '"operation": "set", "level": 101' in worker_source
assert '"expected_status": "failed"' in session_source
assert '"notes.create"' in session_source
assert '"web.search"' not in worker_source
assert '"RU_WORKSPACE_ROOT": str(workspace_root)' in session_source
assert 'str(self.project_root)' not in workspace_assignment
```

Replace the GUI Harness scenario catalog with exactly `agent-volume-contract`, `agent-cancel`, `agent-stale-result`, and `agent-private-metadata`. Remove `agent-read-only`, `agent-terminal-approval`, and `agent-terminal-reject`: those names claim model-visible internal tools that v1 intentionally disables. Add source-contract assertions that the retired names are absent from the Python CLI, C# Harness program, and UI automation branches. Keep approval-card behavior covered by `AgentApprovalTests`/`AgentApprovalUiContractTests`; the private Broker approval lifecycle is covered separately below.

The synthetic Worker volume request is intentionally schema-invalid so it reaches the canonical binding and returns `failed/invalid_arguments` before Core Audio endpoint acquisition. Its `CHAT_DONE` copy is fixed to `Không thể thực hiện thay đổi âm lượng.` and never interpolates a status or safe summary.

Add a session unit test that creates an owned temporary workspace, performs the notes approval fixture there, and verifies the directory is empty after cleanup. Add a Broker conformance test with a fake volume-controller factory that throws if opened; an out-of-range request must fail schema validation without calling it.

Run:

```powershell
& $ruPython -m pytest -q tests/test_harness_artifacts.py tests/test_harness_session.py tests/test_agent_harness.py
dotnet test backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AdapterConformanceTests"
```

Expected: current artifacts lack evidence class, current completion ignores terminal status, and the Harness points Broker workspace at the repository.

- [ ] **Step 3: Implement deterministic contract and adapter evidence**

Add a required keyword-only `evidence_class: str` to `build_artifact`, validate it against a frozen three-value set, and emit it next to `mode`. Every standard Harness call passes `contract`.

Create `self._workspace_root = self._temporary_root / "workspace"`, create it before process start, pass it as `RU_WORKSPACE_ROOT`, and include only `workspace_fixture: "temporary"` in the artifact. Never serialize the path.

Change `deterministic_worker.py` to emit one request:

```python
WorkerReply(
    CHAT_TOOL_REQUEST,
    {
        "request_id": "harness-tool-volume-invalid",
        "session_id": session_id,
        "correlation_id": correlation_id,
        "tool_id": "system.volume",
        "tool_version": "1.0.0",
        "capability_class": "native_mutation",
        "arguments": {"operation": "set", "level": 101},
        "deadline_ms": 30_000,
    },
)
```

Require the returned context IDs and outer `failed` status, then emit only the fixed failure copy. Refactor the approval probe to connect directly to the authenticated private Broker protocol, open only the `workspace.write` scope, send `notes.create` inside the temporary workspace, approve once, replay the same approval once, and close the session in `finally`. Run this private approval probe before the public chat tool-loop probe because the v1 Broker owns one active session; assert the close result before allowing the Host to open its volume session. It records only lifecycle booleans and terminal statuses. The deterministic Worker no longer emits `web.search`, `files.list`, terminal commands, or approval fixtures.

Update the Python/C# GUI Harness scenario allowlists and remove the approval-click branch and now-unreferenced approval-button helper from `UiAutomationScenario.cs`. `agent-volume-contract` asserts the exact terminal `failed` action status and fixed truthful reply. Cancel/stale/privacy scenarios keep their existing lifecycle purpose but use the same non-native volume fixture. This is a claim correction, not a reduction of production approval UI code.

`AdapterConformanceTests` uses temporary directories and real bounded file/notes/terminal adapters to prove: write then readback; cleanup; exit zero succeeds; non-zero, timeout, and start failure produce outer `failed`; invalid volume arguments never open an endpoint; a fake endpoint readback mismatch produces outer `failed`. It must never instantiate `WindowsCoreAudioVolumeController`.

- [ ] **Step 4: Write failing native-script safety tests**

Create `test_native_volume_acceptance.py` around injected process, HTTP, WebSocket, and artifact-writer seams. Prove:

1. no flag exits `2` before allocating a port or spawning a process;
2. a Broker path outside a published directory or without `RuAgent.Broker.exe` exits `2`;
3. correct bearer succeeds and a random wrong bearer gets `401` before WebSocket use;
4. an original state is always restored after success, tool failure, timeout, malformed receipt, and `KeyboardInterrupt`;
5. failure to restore makes the native artifact fail with `restored: false` and exit `1`;
6. artifacts contain only evidence class, role, endpoint hash, statuses, durations, cleanup/restoration booleans, and stable error codes;
7. levels, mute values, token, descriptor path, executable path, device name, arguments, output, and native exception text are absent.

The test fake records operation order and requires:

```python
assert calls == ["get-original", "change-one-point", "verify-change", "restore", "verify-restore"]
```

Run:

```powershell
& $ruPython -m pytest -q tests/test_native_volume_acceptance.py
```

Expected: import failure because the opt-in runner does not exist.

- [ ] **Step 5: Implement the published-Broker native runner without running it**

Use these explicit CLI arguments:

```text
--broker-exe <absolute published RuAgent.Broker.exe>
--output <absolute json path>
--confirm-native-volume-change
--timeout-seconds <5..60, default 20>
```

The implementation has four small owners:

```python
@dataclass(frozen=True, slots=True)
class NativeRunConfig:
    broker_exe: Path
    output: Path
    timeout_seconds: float
```

`PublishedBrokerProcess` owns `start`, `wait_ready`, and `close`; `BrokerProtocolClient` owns async `open`, `execute_volume`, `approve`, and `close`; `NativeAcceptanceRunner` owns the capture/change/verify/restore transaction and safe artifact result. The process owner allocates one loopback port, writes a descriptor under `tempfile.TemporaryDirectory`, generates a 32-byte token, starts only the supplied published executable with `CREATE_NO_WINDOW`, and sets only `RU_AGENT_BROKER_DESCRIPTOR` plus a temporary `RU_WORKSPACE_ROOT`. It waits for authenticated `/health`, proves a fresh wrong bearer returns `401`, and kills only its owned process tree during cleanup.

The protocol client connects to `/agent` with bearer auth, validates hello capabilities, opens `system.write` with no skills, and validates every context ID/status/verification field. If a mutation returns `approval_required`, send one matching approved response and validate the terminal receipt.

The run sequence gets original state, chooses `increase 1` unless the level is 100 and then chooses `decrease 1`, requires verified readback on the same role/hash, restores original level and mute state in `finally`, verifies restoration, closes the session, and removes descriptor/temp files. The artifact is built with `evidence_class="native"` and contains no state values. Exit `0` only when mutation proof and restoration both pass.

Do not invoke the script during implementation or automated verification.

- [ ] **Step 6: Run deterministic acceptance tests and commit**

```powershell
& $ruPython -m pytest -q tests/test_harness_artifacts.py tests/test_harness_session.py tests/test_agent_harness.py tests/test_native_volume_acceptance.py
dotnet test backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AdapterConformanceTests|FullyQualifiedName~VolumeToolsTests|FullyQualifiedName~TerminalAdapterTests|FullyQualifiedName~ProcessTreeRunnerTests"
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentApprovalTests|FullyQualifiedName~AgentApprovalUiContractTests"
dotnet build gui-winui3.Harness\RuApp.Harness.csproj -c Release --no-restore --nologo
```

Expected: all tests pass without touching the current audio endpoint.

```powershell
git add -- scripts/harness/artifacts.py scripts/harness/deterministic_worker.py scripts/harness/session.py scripts/aspnetcore_compatibility_harness.py gui-winui3.Harness/Program.cs gui-winui3.Harness/UiAutomationScenario.cs scripts/native_volume_acceptance.py backend-dotnet/RuAgent.Broker.Tests/AdapterConformanceTests.cs tests/test_harness_artifacts.py tests/test_harness_session.py tests/test_agent_harness.py tests/test_native_volume_acceptance.py
git commit -m "test: separate action acceptance evidence classes"
```

---

### Task 11: Reconcile operating documents and run all deterministic release gates

**Files:**
- Modify: `README.md`
- Modify: `docs\operations\agent-tools.md`
- Modify: `docs\operations\verification.md`
- Modify: `docs\README.md`
- Retired volume-slice design/plan are not part of the current documentation set; publish the resulting contract in `docs\operations\agent-tools.md` and `docs\operations\verification.md`.
- Modify: `docs\superpowers\plans\2026-08-22-ru-agent-tools-skills.md`
- Modify after all gates pass: `docs\superpowers\specs\2026-08-22-ru-action-execution-kernel-v1-design.md`
- Modify after all gates pass: `docs\superpowers\plans\2026-08-22-ru-action-execution-kernel-v1.md`
- Modify: `tests\test_current_documentation.py`
- Apply additively in the primary checkout after integration, without staging: `D:\LLM\docs\operations\logging.md`
- Apply additively in the primary checkout after integration, without staging: `D:\LLM\docs\operations\runtime-behavior.md`

**Interfaces:**
- Current operating docs distinguish executable, model-enabled, deferred, and disabled capability states.
- Verification docs separate `contract`, `adapter`, and `native` claims and never imply device success from tests.
- Historical volume documents are retained but visibly superseded; no current index points to them as operating truth.
- The two already-dirty operations documents receive additive, conflict-aware updates only in the primary checkout.

- [ ] **Step 1: Write failing current-documentation assertions**

Add assertions that read current docs and require:

```python
assert "| `system.volume@1.0.0` | `native_pending` | yes | `system.write` |" in agent_tools
assert "| Skills | `disabled` | no | none |" in agent_tools
assert "| `app.open` | `absent` | no | none |" in agent_tools
assert "evidence_class: contract" in verification
assert "evidence_class: adapter" in verification
assert "evidence_class: native" in verification
assert "tests/test_volume_control.py" not in verification
assert "pycaw" not in agent_tools.casefold()
assert "comtypes" not in agent_tools.casefold()
assert "Superseded" in old_volume_design
assert "operations/agent-tools.md" in docs_index
assert "agent-volume-contract" in root_readme
assert "agent-terminal-approval" not in root_readme
```

Also assert current docs do not say that Web, calendar, tasks, mail, `app.open`, terminal, file tools, or skills are model-usable. Use direct phrases and table rows rather than broad substring bans that would reject a clearly labeled deferred row.

Run:

```powershell
& $ruPython -m pytest -q tests/test_current_documentation.py
```

Expected: current docs still describe the aspirational catalog, Python volume tests, markers, and skill packages as usable.

- [ ] **Step 2: Rewrite the clean operating documents as current truth**

`agent-tools.md` must contain one canonical table with these exact states:

| Capability | Availability | Model enabled | Scope |
| --- | --- | --- | --- |
| `system.volume@1.0.0` | `native_pending` | yes | `system.write` |
| `system.time@1.0.0` | `executable` | no, Broker-internal | `system.read` |
| `system.status@1.0.0` | `executable` | no, Broker-internal | `system.read` |
| `app.list@1.0.0` | `executable` | no, Broker-internal | `system.read` |
| `files.list@1.0.0` | `executable` | no, Broker-internal | `workspace.read` |
| `files.read@1.0.0` | `executable` | no, Broker-internal | `workspace.read` |
| `files.write@1.0.0` | `executable` | no, Broker-internal | `workspace.write` |
| `terminal.run@1.0.0` | `executable` | no, Broker-internal | `terminal.execute` |
| `terminal.shell@1.0.0` | `executable` | no, Broker-internal | `terminal.execute` |
| `notes.create@1.0.0` | `executable` | no, Broker-internal | `workspace.write` |
| `app.open` | `absent` | no | none |
| Workspace search, Web, calendar, tasks, mail | `absent` | no | none |
| Skills | `disabled` | no | none |

Document the one-path ownership chain, actual `agent.session` grant, one-tool-per-turn limit, verified mutation rule, legacy marker translation without execution, disabled skills, terminal outer-status conformance, and `native_pending` meaning. Remove the old Python marker-execution and broad capability claims.

`verification.md` must show three separate sections with literal labels `evidence_class: contract`, `evidence_class: adapter`, and `evidence_class: native`. Contract runs the deterministic Harness and proves protocol/lifecycle only. Adapter runs fake/temporary-fixture tests and proves result semantics. Native shows the opt-in command but says it is not part of CI/default build and still does not replace user-perceived GUI acceptance.

Add `Agent tools and truthful execution` to `docs/README.md` linking `operations/agent-tools.md`. Update the root README's deterministic GUI Harness list to the four Task 10 scenarios and explain that Broker approval replay and GUI approval-card contracts are separate deterministic checks. Keep the dated documents under the historical section only.

- [ ] **Step 3: Mark superseded plans accurately**

At the top of the old volume design, replace its status with:

```markdown
Status: Superseded 2026-08-22 by [Ru Action Execution Kernel v1](../specs/2026-08-22-ru-action-execution-kernel-v1-design.md). Do not implement this dual Python/C# action path.
```

Add the equivalent `Superseded; do not execute` notice to the old volume implementation plan, linking both the approved design and this implementation plan.

Replace the broad execution-status block in `ru-agent-tools-skills.md` with a factual boundary: Broker/process/protocol scaffolding exists, but the prior broad usable-tool/skill claim is withdrawn; v1 exposes only model-enabled volume; ten bindings are internal/executable; `app.open`, connectors, and eight unbound tools are absent; skills are disabled; future vertical slices require their own receipts and acceptance.

Do not delete those historical documents: their rationale remains useful and inbound references remain valid once the status is corrected.

- [ ] **Step 4: Run focused documentation and stale-reference checks**

```powershell
& $ruPython -m pytest -q tests/test_current_documentation.py tests/test_python_package_architecture.py
rg -n "tests/test_volume_control\.py|pycaw|comtypes|normal-chat marker execution|first usable tool set|skills are declarative" docs README.md
```

Expected: tests pass. Search results are permitted only inside explicitly superseded historical files; every such hit must be within a document whose first 12 lines contain `Superseded` or a roadmap/non-shipped warning. Any hit in current docs is a failure.

- [ ] **Step 5: Restore and run the complete deterministic test matrix sequentially**

Run one command at a time and retain fresh exit output:

```powershell
dotnet restore backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo
dotnet restore backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --nologo
dotnet restore backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj --nologo
dotnet restore gui-winui3.Tests\gui-winui3.Tests.csproj --nologo
dotnet restore gui-winui3.Harness\RuApp.Harness.csproj --nologo
& $ruPython -m pytest -q
dotnet test backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
dotnet test backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj -c Release --no-restore --nologo
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo
dotnet build gui-winui3.Harness\RuApp.Harness.csproj -c Release --no-restore --nologo
& $ruPython scripts/check_aspnetcore_route_matrix.py
```

Expected: every command exits 0. Route matrix remains `41 HTTP + 2 WebSocket`; this slice adds no public route. If any gate fails, fix it in the owning task's files, add a focused regression test, rerun the failed gate, then rerun this full sequence from the first Python command. Do not weaken or skip tests.

- [ ] **Step 6: Run the deterministic contract Harness**

Use a temporary artifact path outside the repository:

```powershell
$actionKernelArtifact = Join-Path ([System.IO.Path]::GetTempPath()) 'ru-action-kernel-contract.json'
& $ruPython scripts/aspnetcore_compatibility_harness.py --mode backend-deterministic --output $actionKernelArtifact
$actionKernelReport = Get-Content -Raw -LiteralPath $actionKernelArtifact | ConvertFrom-Json
if ($actionKernelReport.evidence_class -ne 'contract') { throw 'wrong evidence class' }
if ($actionKernelReport.summary.status -ne 'passed') { throw 'contract Harness failed' }
```

Expected: Harness exits 0, reports `evidence_class: contract`, exact terminal tool status, approval lifecycle, privacy, ownership, port closure, descriptor removal, and temporary cleanup. It must contain no native-success claim and must not access or mutate an audio endpoint.

- [ ] **Step 7: Publish the complete product without native execution**

```powershell
cmd /c build.bat
```

Expected: exit 0 and these three files exist freshly:

```powershell
$brokerExe = 'backend-dotnet\RuAgent.Broker\bin\Release\net10.0\win-x64\publish\RuAgent.Broker.exe'
$backendExe = 'backend-dotnet\RuBackend\bin\Release\net10.0\win-x64\publish\RuBackend.exe'
$guiExe = 'gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe'
if (-not ((Test-Path $brokerExe) -and (Test-Path $backendExe) -and (Test-Path $guiExe))) { throw 'publish artifact missing' }
```

Do not run `native_volume_acceptance.py`. A successful publish is not evidence that Windows volume changed.

- [ ] **Step 8: Mark this slice implemented and commit clean documentation**

Only after Steps 5–7 pass, change the approved design status to `Implemented 2026-08-22; native GUI/audio acceptance remains user-owned` and add the same implementation status below this plan's title. Check every current document one more time; remove a document only if all its content is superseded and all inbound references are updated in the same commit. For this slice, the expected decision is to retain the two corrected historical documents.

Run:

```powershell
git diff --check
git status --short
git diff --name-only HEAD
```

Expected: only task-owned implementation/docs files and generated ignored build outputs appear; no user-owned primary-checkout change exists in the isolated worktree.

```powershell
git add -- README.md docs/operations/agent-tools.md docs/operations/verification.md docs/README.md docs/superpowers/plans/2026-08-22-ru-agent-tools-skills.md docs/superpowers/specs/2026-08-22-ru-action-execution-kernel-v1-design.md docs/superpowers/plans/2026-08-22-ru-action-execution-kernel-v1.md tests/test_current_documentation.py
git commit -m "docs: publish truthful action capability boundaries"
```

- [ ] **Step 9: Apply the two conflict-aware operating-doc additions after integration**

After the implementation branch is integrated into `D:\LLM\main`, re-read the current dirty versions and use `apply_patch` to add this section to `docs/operations/logging.md` without replacing any user content:

```markdown
## Action receipt diagnostics

The bounded action trail uses `tool.requested`, `tool.policy.denied`,
`tool.approval.required`, `tool.adapter.started`, `tool.adapter.completed`,
`tool.verification.succeeded`, `tool.verification.failed`,
`tool.receipt.delivered`, and `tool.receipt.rejected`.

Allowed action fields are correlation ID, tool ID/version, terminal status,
stable safe error code, duration, verification state, the fixed endpoint role,
and an ephemeral per-Broker endpoint hash. Arguments, output values, prompts,
replies, paths, scripts, stdout/stderr, credentials, device names, and native
exception text are forbidden.
```

Add this section to `docs/operations/runtime-behavior.md` the same way:

```markdown
## Action execution kernel v1

Normal-chat and Agent-mode volume requests use the same path: the Worker makes
one non-visible structured selection, the Public Host opens only the scope in
the Broker capability, and RuAgentBroker executes and verifies the native
adapter. The Worker may render success only from a matching `succeeded` receipt
whose verification state is `verified`. Python does not own desktop side
effects.

Only `system.volume@1.0.0` is model-enabled in v1. File, terminal, notes, and
read-only system bindings remain Broker-internal; unbound connectors and
`app.open` are absent; skills are disabled. The GUI renders the actual
`agent.session` grant received from the Host.
```

Then run only the documentation check against the integrated primary checkout:

```powershell
& $ruPython -m pytest -q tests/test_current_documentation.py
git diff --check -- docs/operations/logging.md docs/operations/runtime-behavior.md
git status --short
```

Expected: the doc test passes and both files remain unstaged together with the user's pre-existing edits. Report their uncommitted state explicitly; do not commit or overwrite them without separate authorization.

---

## User-owned acceptance after deterministic completion

The automated implementation stops before changing the machine's volume. Hand the user these two explicit choices:

1. Run the opt-in published-Broker native smoke with `--confirm-native-volume-change`, then inspect its `native` artifact and verify restoration.
2. Launch the published GUI and ask Ru for one bounded volume change in normal chat and one in Agent mode, verify the Windows playback level actually changes, verify the Session Grant displays only `system.write`, and verify an unavailable/readback-failure case produces no success claim.

Neither choice is executed or reported as passed unless the user explicitly authorizes it and observes the result.
