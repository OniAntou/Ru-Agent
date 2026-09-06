# Agent Tool Loop Reliability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make model-generated tool calls parse reliably, execute at most once when repeated unchanged, retain workspace membership throughout slow steps, and preserve user-supplied external paths.

**Architecture:** Keep format compatibility in the Worker parser, duplicate protection in durable Harness coordination, execution authority in the Broker, and lease liveness in a focused background heartbeat owned by the runtime loop. Provider errors remain explicit and never trigger provider/model fallback.

**Tech Stack:** .NET 10, C#, xUnit, ASP.NET Core BackgroundService, SQLite workspace coordination, WinUI 3 live acceptance.

## Global Constraints

- Do not add API, provider, model, or paid-model fallback.
- Broker remains the only authority that canonicalizes, authorizes, and executes tools.
- External-directory grants remain process-local and isolated by Agent session.
- Preserve unrelated dirty work and stage only files changed by this plan.
- Run heavyweight test projects sequentially.
- Use an explicitly free model for live GUI acceptance.

---

## File Structure

- `backend-dotnet/RuAgent.Harness.Core/Prompting/ModelToolSelectionParser.cs`: strict normalization of supported model text wrappers.
- `backend-dotnet/RuAgent.Harness.Core/Prompting/ConsecutiveToolCallGuard.cs`: pure durable-context comparison for duplicate calls.
- `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs`: records and terminates a repeated call before Broker dispatch.
- `backend-dotnet/RuBackend/Harness/Workspace/WorkspaceMembershipHeartbeat.cs`: isolated lease-renewal lifetime helper.
- `backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs`: owns heartbeat while a run is actively driven.
- `backend-dotnet/RuAgent.Protocol/AgentToolDefinitionCatalog.cs`: accurate relative/absolute file path guidance for models.
- Corresponding `*.Tests` files: regression coverage for every behavior.
- `docs/operations/agent-tools.md` and `docs/architecture.md`: operator and architecture contracts.

### Task 1: Parse exact `<tool_call>` wrappers

**Files:**
- Modify: `backend-dotnet/RuAgent.Harness.Core/Prompting/ModelToolSelectionParser.cs`
- Test: `backend-dotnet/RuAgent.Harness.Core.Tests/Prompting/ModelToolSelectionParserTests.cs`
- Test: `backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerV2StepServiceTests.cs`

**Interfaces:**
- Consumes: `ModelToolSelectionParser.NormalizeCandidate(string text)`.
- Produces: the same method returning the inner JSON only for one exact, attribute-free `<tool_call>` wrapper.

- [ ] **Step 1: Add failing parser tests**

Add a success case for an exact case-insensitive wrapper and rejection-as-unchanged cases for malformed forms:

```csharp
[Fact]
public void NormalizeCandidate_unwraps_one_exact_tool_call_element()
{
    const string json = "{\"tool_id\":\"files.read\",\"tool_version\":\"1.0.0\",\"arguments\":{\"path\":\"README.md\"}}";
    Assert.Equal(json, ModelToolSelectionParser.NormalizeCandidate($"<tool_call> {json} </tool_call>"));
    Assert.Equal(json, ModelToolSelectionParser.NormalizeCandidate($"<TOOL_CALL>\n{json}\n</TOOL_CALL>"));
}

[Theory]
[InlineData("prefix <tool_call>{}</tool_call>")]
[InlineData("<tool_call kind=\"x\">{}</tool_call>")]
[InlineData("<tool_call><tool_call>{}</tool_call></tool_call>")]
[InlineData("<tool_call>{}</tool_call> suffix")]
[InlineData("<tool_call>{}")]
public void NormalizeCandidate_does_not_unwrap_ambiguous_tool_call_markup(string text) =>
    Assert.Equal(text, ModelToolSelectionParser.NormalizeCandidate(text));
```

Add a worker test whose provider emits a split wrapped selection and assert `Text` and streamed deltas are empty while one `files.read` call is returned.

- [ ] **Step 2: Run RED tests**

Run sequentially:

```powershell
dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --filter FullyQualifiedName~ModelToolSelectionParserTests
dotnet test backend-dotnet/RuAgent.Runtime.Worker.Tests/RuAgent.Runtime.Worker.Tests.csproj --filter FullyQualifiedName~WorkerV2StepServiceTests
```

Expected: exact-wrapper tests fail because the candidate still begins with `<`; existing tests remain green.

- [ ] **Step 3: Implement exact wrapper normalization**

After think-tag removal and before Markdown-fence removal, recognize only an opening tag exactly equal to `<tool_call>` and a final closing tag exactly equal to `</tool_call>`, using ordinal case-insensitive comparison. Trim the body, require it to begin with `{`, and refuse unwrapping if the body contains another opening or closing tool-call tag.

- [ ] **Step 4: Run GREEN tests and commit**

Run both commands from Step 2, then:

```powershell
git add -- backend-dotnet/RuAgent.Harness.Core/Prompting/ModelToolSelectionParser.cs backend-dotnet/RuAgent.Harness.Core.Tests/Prompting/ModelToolSelectionParserTests.cs backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerV2StepServiceTests.cs
git commit -m "fix(agent): parse exact tool call wrappers"
```

### Task 2: Stop exact consecutive tool-call loops before execution

**Files:**
- Create: `backend-dotnet/RuAgent.Harness.Core/Prompting/ConsecutiveToolCallGuard.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs`
- Test: `backend-dotnet/RuAgent.Harness.Core.Tests/Prompting/ConsecutiveToolCallGuardTests.cs`
- Test: `backend-dotnet/RuAgent.Harness.Core.Tests/AgentRunCoordinatorTests.cs`

**Interfaces:**
- Consumes: `ModelContextSnapshot` and proposed `IReadOnlyList<ModelToolCall>`.
- Produces: `ConsecutiveToolCallGuard.IsRepeated(ModelContextSnapshot context, IReadOnlyList<ModelToolCall> proposed) : bool`.

- [ ] **Step 1: Add failing pure guard tests**

Cover identical canonical objects with different property order, changed arguments, changed tool identity, no prior receipt, and a prior non-tool entry. The expected duplicate shape is a final prior `AssistantStep` followed only by terminal `ToolResult` entries, with the same ordered calls as the new proposal.

```csharp
Assert.True(ConsecutiveToolCallGuard.IsRepeated(contextWithCompletedRead, [sameRead]));
Assert.False(ConsecutiveToolCallGuard.IsRepeated(contextWithCompletedRead, [changedRead]));
```

- [ ] **Step 2: Run the guard test and verify RED**

```powershell
dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --filter FullyQualifiedName~ConsecutiveToolCallGuardTests
```

Expected: compilation fails because `ConsecutiveToolCallGuard` does not exist.

- [ ] **Step 3: Implement the pure guard**

Canonicalize arguments with deterministic object-key ordering, compare tool ID/version/arguments, ignore provider call IDs, and require at least one terminal tool receipt after the prior assistant step. Keep this class stateless so restart behavior derives from durable context.

- [ ] **Step 4: Add failing coordinator integration test**

Queue two identical model tool steps. Assert the gateway executes once, the second proposal never reaches it, and the run ends failed with `repeated_tool_call`. Also queue changed arguments and assert both execute.

- [ ] **Step 5: Run the coordinator test and verify RED**

```powershell
dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --filter "FullyQualifiedName~AgentRunCoordinatorTests&Name~Repeated"
```

Expected: duplicate execution count is 2 or the run remains running.

- [ ] **Step 6: Persist the repeated step and fail closed**

In `RunOneStepAsync`, after validating the prompt receipt and before `HandleToolStepAsync`, call the guard. Add a focused private commit method that stores the prompt receipt and assistant step for audit, appends `ModelPromptComposed`, `ModelStepCompleted` with safe code `repeated_tool_call`, and `RunFailed` with the same code in one commit. It must not reserve tool-call budget or call `_tools.ExecuteAsync`.

- [ ] **Step 7: Run GREEN tests and commit**

Run all Harness Core tests, then:

```powershell
git add -- backend-dotnet/RuAgent.Harness.Core/Prompting/ConsecutiveToolCallGuard.cs backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs backend-dotnet/RuAgent.Harness.Core.Tests/Prompting/ConsecutiveToolCallGuardTests.cs backend-dotnet/RuAgent.Harness.Core.Tests/AgentRunCoordinatorTests.cs
git commit -m "fix(agent): stop repeated tool calls"
```

### Task 3: Keep workspace membership alive during slow steps

**Files:**
- Create: `backend-dotnet/RuBackend/Harness/Workspace/WorkspaceMembershipHeartbeat.cs`
- Modify: `backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs`
- Create: `backend-dotnet/RuBackend.Tests/Harness/WorkspaceMembershipHeartbeatTests.cs`

**Interfaces:**
- Produces: `WorkspaceMembershipHeartbeat.Start(Func<CancellationToken, Task<bool>> renewAsync, TimeSpan leaseDuration, CancellationToken ownerToken)`.
- Produces: `Task Lost` completing only when renewal returns false or throws.
- Produces: `ValueTask DisposeAsync()` cancelling and awaiting the renewal loop.

- [ ] **Step 1: Add failing heartbeat tests**

Use a short lease and thread-safe counters. Assert renewal happens while simulated work is blocked, disposal stops renewals, and a false renewal completes `Lost` once.

```csharp
await using var heartbeat = WorkspaceMembershipHeartbeat.Start(
    _ => Task.FromResult(Interlocked.Increment(ref renewals) < 3),
    TimeSpan.FromMilliseconds(90),
    CancellationToken.None);
await heartbeat.Lost.WaitAsync(TimeSpan.FromSeconds(2));
Assert.Equal(3, Volatile.Read(ref renewals));
```

- [ ] **Step 2: Run the heartbeat tests and verify RED**

```powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter FullyQualifiedName~WorkspaceMembershipHeartbeatTests
```

Expected: compilation fails because the heartbeat type does not exist.

- [ ] **Step 3: Implement the heartbeat helper**

Renew immediately, then every `max(50 ms, leaseDuration / 3)` using `PeriodicTimer`. Link owner cancellation to a private token. Capture renewal failure in `Lost` without throwing on disposal. Reject non-positive lease durations.

- [ ] **Step 4: Integrate heartbeat with the runtime loop**

After membership acquisition, start the heartbeat with the store renewal callback. Race every `RunOneStepAsync` against `heartbeat.Lost`. If the heartbeat wins, cancel the step through a linked token, await its shutdown, and fail the run with `workspace_membership_expired`. Dispose the heartbeat before releasing membership and path claims. Remove the current between-step renewal block.

- [ ] **Step 5: Run GREEN tests and commit**

Run `RuBackend.Tests` sequentially, then:

```powershell
git add -- backend-dotnet/RuBackend/Harness/Workspace/WorkspaceMembershipHeartbeat.cs backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs backend-dotnet/RuBackend.Tests/Harness/WorkspaceMembershipHeartbeatTests.cs
git commit -m "fix(agent): renew workspace lease during slow steps"
```

### Task 4: Correct the model-facing file path contract

**Files:**
- Modify: `backend-dotnet/RuAgent.Protocol/AgentToolDefinitionCatalog.cs`
- Modify: `backend-dotnet/RuAgent.Protocol.Tests/AgentToolDefinitionCatalogTests.cs`

**Interfaces:**
- Produces: descriptions stating that `path` may be workspace-relative or an exact absolute path inside a session-granted directory.

- [ ] **Step 1: Add a failing catalog contract test**

```csharp
[Fact]
public void File_tool_descriptions_preserve_user_supplied_absolute_paths()
{
    foreach (var definition in AgentToolDefinitionCatalog.Get().Where(x => x.ToolId.StartsWith("files.", StringComparison.Ordinal) && x.ToolId != "files.restore"))
    {
        Assert.Contains("absolute", definition.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preserve", definition.Description, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run the catalog test and verify RED**

```powershell
dotnet test backend-dotnet/RuAgent.Protocol.Tests/RuAgent.Protocol.Tests.csproj --filter FullyQualifiedName~File_tool_descriptions_preserve_user_supplied_absolute_paths
```

- [ ] **Step 3: Update descriptions without changing schemas**

Use one shared sentence fragment for file tools: `Use a workspace-relative path, or preserve an exact absolute path supplied by the user inside a directory granted for this session.` Append operation-specific guidance. Keep `files.restore` recovery-ID based.

- [ ] **Step 4: Run GREEN tests and commit**

```powershell
dotnet test backend-dotnet/RuAgent.Protocol.Tests/RuAgent.Protocol.Tests.csproj
git add -- backend-dotnet/RuAgent.Protocol/AgentToolDefinitionCatalog.cs backend-dotnet/RuAgent.Protocol.Tests/AgentToolDefinitionCatalogTests.cs
git commit -m "fix(agent): clarify external file paths"
```

### Task 5: Documentation, full verification, build, and live acceptance

**Files:**
- Modify: `docs/operations/agent-tools.md`
- Modify: `docs/architecture.md`

**Interfaces:**
- Consumes: final behavior from Tasks 1–4.
- Produces: current operator and architecture guidance indexed by `help.docs`.

- [ ] **Step 1: Update focused documentation hunks**

Document exact wrapper compatibility, duplicate-call termination, continuously renewed membership during a run, session-scoped external paths, and explicit provider failures without fallback. Preserve unrelated existing edits in both files.

- [ ] **Step 2: Run focused suites sequentially**

```powershell
dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj
dotnet test backend-dotnet/RuAgent.Runtime.Worker.Tests/RuAgent.Runtime.Worker.Tests.csproj
dotnet test backend-dotnet/RuAgent.Protocol.Tests/RuAgent.Protocol.Tests.csproj
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj
```

- [ ] **Step 3: Build and publish the GUI**

Close the live published GUI/backend processes first so publish files are not locked, then run the repository's documented WinUI publish command. Confirm exit code 0 and relaunch the published `RuApp.exe` from `D:\LLM`.

- [ ] **Step 4: Repeat live GUI acceptance with a free model**

Create a new session and target a fresh subdirectory under `D:\Test Agent`. Verify: exact wrapped tool selection executes without visible XML; one external-directory approval; same-session reuse; new-session re-approval; write/edit/read/list/glob/search; safe `git status`; `help.docs`; `web.fetch`; deliberate duplicate call is blocked before a second execution; Stop cancels; no file appears in `D:\LLM`.

- [ ] **Step 5: Inspect documentation and repository state**

Run `git diff --check`, inspect all changed docs for stale claims, and verify `git status --short --branch` contains no accidental test artifacts.

- [ ] **Step 6: Commit owned documentation only**

Stage only the exact documentation hunks produced by this task, preserving unrelated dirty hunks, then commit:

```powershell
git commit -m "docs(agent): document reliable tool execution"
```
