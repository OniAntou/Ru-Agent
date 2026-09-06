# Agent Free-Model Tool Grounding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reliably execute free-model tool selections, reuse session directory grants for terminal work, and prevent unverified prose from masking failed tools.

**Architecture:** Keep model-format compatibility in the strict Worker parser, choose an effective confined root inside the Broker for every filesystem-sensitive adapter, and enforce grounded finalization in the durable Harness coordinator. Broker remains the only execution authority, and provider failures remain explicit without fallback.

**Tech Stack:** .NET 10, C#, xUnit, ASP.NET Core, WebSocket Broker protocol, WinUI 3, SQLite Harness persistence.

## Global Constraints

- Do not add provider, model, API, or paid-model fallback.
- Broker remains the only authority that canonicalizes, authorizes, and executes tools.
- Never disable terminal confinement to make an external directory work.
- Directory grants remain process-local, Agent-session-scoped, and bounded to their canonical directory.
- A new chat or application restart must not inherit a directory grant.
- Preserve unrelated dirty work and stage only files changed by this plan.
- Run heavyweight test projects sequentially.
- Use only explicitly free models for live GUI acceptance.

---

## File Structure

- `backend-dotnet/RuAgent.Harness.Core/Prompting/ModelToolSelectionParser.cs`: bounded normalization for complete wrapped JSON selections.
- `backend-dotnet/RuAgent.Runtime.Worker/WorkerV2StepService.cs`: existing stream holdback and final prompt-selection conversion; production changes are expected only if the parser test exposes an additional boundary defect.
- `backend-dotnet/RuAgent.Harness.Core/Prompting/FailedToolCompletionGuard.cs`: pure inspection of trailing committed tool receipts.
- `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs`: suppresses unverified final prose and commits a terminal run failure.
- `backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs`: selects the effective granted root for terminal adapters and formats their result against that root.
- Corresponding `*.Tests` files: focused parser, Worker, Broker-host, guard, and coordinator regressions.
- `docs/operations/agent-tools.md`, `docs/architecture.md`, and the approved spec: current behavior and operator contract.

### Task 1: Parse a complete tool selection with an omitted close tag

**Files:**
- Modify: `backend-dotnet/RuAgent.Harness.Core/Prompting/ModelToolSelectionParser.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core.Tests/Prompting/ModelToolSelectionParserTests.cs`
- Modify: `backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerV2StepServiceTests.cs`
- Modify only if required by the RED result: `backend-dotnet/RuAgent.Runtime.Worker/WorkerV2StepService.cs`

**Interfaces:**
- Consumes: `ModelToolSelectionParser.NormalizeCandidate(string text)` and `ModelToolSelectionParser.Parse(string text, IReadOnlySet<string> advertisedToolIds)`.
- Produces: normalization of one leading `<tool_call>` plus one complete JSON object, with or without the exact closing tag.

- [ ] **Step 1: Change the parser contract test first**

Replace the old missing-close rejection case with a success assertion using a fully valid selection, while retaining rejection cases for prose, attributes, nesting, suffixes, multiple wrappers, and incomplete JSON:

```csharp
[Fact]
public void NormalizeCandidate_unwraps_complete_json_after_one_open_tool_call_tag()
{
    const string json = "{\"tool_id\":\"terminal.run\",\"tool_version\":\"1.0.0\",\"arguments\":{\"executable\":\"python\"}}";
    Assert.Equal(json, ModelToolSelectionParser.NormalizeCandidate($"<tool_call> {json}"));
}

[Theory]
[InlineData("prefix <tool_call>{}")]
[InlineData("<tool_call kind=\"x\">{}")]
[InlineData("<tool_call><tool_call>{}")]
[InlineData("<tool_call>{} suffix")]
[InlineData("<tool_call>{")]
public void NormalizeCandidate_rejects_ambiguous_unclosed_markup(string text) =>
    Assert.Equal(text, ModelToolSelectionParser.NormalizeCandidate(text));
```

- [ ] **Step 2: Add the Worker regression test**

Feed the exact live shape in split deltas—`<tool_` followed by `call> {valid JSON}` with no close tag—and assert the result contains one `terminal.run` call, `Text` is empty, streamed visible deltas are empty, and `FinishReason` is `tool_calls`.

- [ ] **Step 3: Run both focused tests and verify RED**

```powershell
dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --filter FullyQualifiedName~ModelToolSelectionParserTests
dotnet test backend-dotnet/RuAgent.Runtime.Worker.Tests/RuAgent.Runtime.Worker.Tests.csproj --filter FullyQualifiedName~WorkerV2StepServiceTests
```

Expected: the complete missing-close selection remains wrapped or visible and the new assertions fail.

- [ ] **Step 4: Implement bounded normalization**

After think-tag removal, detect an opening tag exactly equal to `<tool_call>`. If an exact final closing tag exists, preserve the existing behavior. If it does not, trim the body and remove the opening tag only when the body begins with `{`, contains no opening or closing tool tag, and `JsonDocument.Parse(body)` consumes one root JSON object. Do not validate tool identity in normalization; the existing `Parse` method remains authoritative.

- [ ] **Step 5: Run GREEN tests and commit**

Run both commands from Step 3, then:

```powershell
git add -- backend-dotnet/RuAgent.Harness.Core/Prompting/ModelToolSelectionParser.cs backend-dotnet/RuAgent.Harness.Core.Tests/Prompting/ModelToolSelectionParserTests.cs backend-dotnet/RuAgent.Runtime.Worker/WorkerV2StepService.cs backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerV2StepServiceTests.cs
git commit -m "fix(agent): parse complete unclosed tool wrappers"
```

Stage `WorkerV2StepService.cs` only if the RED evidence required a production change there.

### Task 2: Execute terminal calls inside a session-granted directory

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs`
- Modify if a focused unit seam is needed: `backend-dotnet/RuAgent.Broker.Tests/ToolExecutionServiceTests.cs`

**Interfaces:**
- Consumes: `ResolveRootToolSet(ToolExecutionContext context, string? pathInput)` and `SessionDirectoryGrantRegistry`.
- Produces: terminal execution through a `RootToolSet` whose `CanonicalRoot` is the run workspace or the longest matching session-granted directory.

- [ ] **Step 1: Add a full Broker protocol regression**

Extend the existing external-directory test pattern:

1. Open run A with a stable Agent session ID.
2. Evaluate an external `files.read`, approve it with `allow_session_directory`, and confirm authorization.
3. Open run B with the same Agent session ID and workspace root.
4. Evaluate `terminal.run` with `working_directory` equal to the external directory, `executable` equal to `dotnet`, arguments `--version`, and `read_only: true`.
5. Prepare and execute the call.
6. Assert policy returns `allow`, execution invokes the adapter, status is `succeeded`, exit code is `0`, and no `workspace_path_outside_root` appears.

Add a paired new-session assertion showing the same terminal request returns `external_directory_required`.

- [ ] **Step 2: Run the focused Broker tests and verify RED**

```powershell
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --filter "FullyQualifiedName~BrokerHostTests&FullyQualifiedName~external_directory"
```

Expected: policy allows the same-session terminal request, but execution fails with `workspace_path_outside_root`.

- [ ] **Step 3: Select the effective terminal root**

In both `ExecuteTerminalRunAsync` and `ExecuteTerminalShellAsync`:

```csharp
var workingDirectory = ReadOptionalString(arguments, "working_directory") ?? ".";
var tools = ResolveRootToolSet(context, workingDirectory);
```

Canonicalize and execute through `tools.Terminal` with confinement enabled against that toolset. Keep protected-location checks and all analyzer/approval behavior unchanged.

- [ ] **Step 4: Share approval-token authority with cached root toolsets**

Change `RootToolSet.Create` to accept the service's `ApprovalTokenService` and pass it to the external-root `TerminalAdapter`. Change `ToolsForRoot` to construct cached toolsets with that same service. This preserves separate terminal-risk approval for unknown or destructive commands inside a granted directory.

- [ ] **Step 5: Format terminal results against the selected root**

Change `ProcessOutcome` to receive the selected canonical root. For a confined result, produce the existing relative working-directory value against that root; otherwise use the existing bounded absolute formatter. Do not call the original `_workspace.ToRelative` for a result rooted in an external grant.

- [ ] **Step 6: Run GREEN tests and commit**

Run the focused Broker test, then the complete Broker suite:

```powershell
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj
git add -- backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs backend-dotnet/RuAgent.Broker.Tests/ToolExecutionServiceTests.cs
git commit -m "fix(agent): confine terminal to granted directories"
```

Stage `ToolExecutionServiceTests.cs` only if it was modified.

### Task 3: Suppress unverified final prose after a failed tool

**Files:**
- Create: `backend-dotnet/RuAgent.Harness.Core/Prompting/FailedToolCompletionGuard.cs`
- Create: `backend-dotnet/RuAgent.Harness.Core.Tests/Prompting/FailedToolCompletionGuardTests.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core.Tests/AgentRunCoordinatorTests.cs`

**Interfaces:**
- Produces: `FailedToolCompletionGuard.TryGetFailure(ModelContextSnapshot context, out string errorCode) : bool`.
- Consumes: trailing `ModelContextEntryKind.ToolResult` entries and their `ToolExecutionReceipt` values.

- [ ] **Step 1: Add pure guard tests**

Cover a trailing failed receipt, mixed trailing receipts where one failed, all-success receipts, a non-tool trailing entry, and a missing/blank receipt code. The fallback for a blank code is `tool_failed`.

```csharp
Assert.True(FailedToolCompletionGuard.TryGetFailure(contextWithFailedTerminal, out var code));
Assert.Equal("workspace_path_outside_root", code);
Assert.False(FailedToolCompletionGuard.TryGetFailure(contextWithSuccessfulTerminal, out _));
```

- [ ] **Step 2: Run the guard tests and verify RED**

```powershell
dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --filter FullyQualifiedName~FailedToolCompletionGuardTests
```

Expected: compilation fails because the guard does not exist.

- [ ] **Step 3: Implement the pure trailing-receipt guard**

Walk backward over only the final contiguous `ToolResult` entries. Return the first unsuccessful stable code in model order; treat `Succeeded` as successful and map a missing/invalid code to `tool_failed`. Return false when the context does not end in a tool result or every trailing receipt succeeded.

- [ ] **Step 4: Add coordinator RED tests**

Seed context ending in a failed receipt, queue an ordinary final assistant step containing a false success claim, and assert:

- the run ends `Failed` with the receipt code;
- no conversation outbox item contains the assistant prose;
- the assistant step is stored only as `InternalOnly` audit payload;
- `ModelStepCompleted` uses safe code `unverified_after_tool_failure`;
- `RunFailed` uses the actual tool error code.

Add a second test where the next model step proposes a different recovery tool and assert it still executes normally.

- [ ] **Step 5: Run coordinator tests and verify RED**

```powershell
dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --filter "FullyQualifiedName~AgentRunCoordinatorTests&FullyQualifiedName~failed_tool"
```

Expected: the prose step currently completes the run and creates a visible outbox entry.

- [ ] **Step 6: Commit grounded failure instead of final prose**

Before `HandleFinalStepAsync`, call the guard. Add `HandleUnverifiedFailureStepAsync` that stores the prompt receipt and assistant step as internal audit payloads, emits `ModelPromptComposed`, `ModelStepCompleted(unverified_after_tool_failure)`, and `RunFailed(errorCode)`, writes no conversation outbox item, and advances the run through `HarnessRunTransition.Fail`.

- [ ] **Step 7: Run GREEN tests and commit**

```powershell
dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj
git add -- backend-dotnet/RuAgent.Harness.Core/Prompting/FailedToolCompletionGuard.cs backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs backend-dotnet/RuAgent.Harness.Core.Tests/Prompting/FailedToolCompletionGuardTests.cs backend-dotnet/RuAgent.Harness.Core.Tests/AgentRunCoordinatorTests.cs
git commit -m "fix(agent): reject unverified success after tool failure"
```

### Task 4: Documentation, build, and live acceptance

**Files:**
- Modify: `docs/operations/agent-tools.md`
- Modify: `docs/architecture.md`
- Modify: `docs/superpowers/specs/2026-09-06-agent-free-model-tool-grounding-design.md` only if implementation reveals a contract correction.

**Interfaces:**
- Consumes: final behavior from Tasks 1–3.
- Produces: current operator/help documentation and a published WinUI build.

- [ ] **Step 1: Update focused documentation hunks**

Document bounded missing-close wrapper compatibility, effective-root terminal confinement for session grants, and failed-tool final grounding. Preserve unrelated dirty hunks in `docs/architecture.md` by staging only owned hunks. Search current help/system-prompt documentation for contradictory claims; update only active documents and remove an outdated document only if it is fully superseded and safe to delete.

- [ ] **Step 2: Run focused suites sequentially**

```powershell
dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj
dotnet test backend-dotnet/RuAgent.Runtime.Worker.Tests/RuAgent.Runtime.Worker.Tests.csproj
dotnet test backend-dotnet/RuAgent.Protocol.Tests/RuAgent.Protocol.Tests.csproj
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj
```

- [ ] **Step 3: Build and publish the GUI**

Close only verified `D:\LLM` Ru processes, run `D:\LLM\build.bat`, require all 13 stages to pass, and confirm the publish timestamp for:

`D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe`

- [ ] **Step 4: Repeat the natural-language GUI scenario with a free model**

Start a new Agent chat, select an explicitly free model, and send one natural-language instruction to create `D:\Test Agent\hello_world.py`, run it with Agent terminal, and report actual output. Approve the external directory once. Verify from durable events and payloads—not model prose alone—that:

- `files.write` succeeds;
- `terminal.run` executes in the same Agent session;
- the terminal receipt has status succeeded, exit code `0`, and stdout `Hello, World!`;
- no `<tool_call>` wrapper appears in visible conversation history;
- no second folder approval is requested;
- no file is created under `D:\LLM`.

- [ ] **Step 5: Verify failure grounding live**

Issue a harmless terminal request with a guaranteed nonexistent executable inside the granted directory. Approve it only if terminal-risk policy asks. Confirm the tool reports its stable failure and any subsequent false-success prose is suppressed; the run must not display fabricated stdout.

- [ ] **Step 6: Inspect docs and repository state**

Run `git diff --check`, inspect `git status --short --branch`, confirm no accidental repository artifacts, and ensure only owned documentation hunks are staged.

- [ ] **Step 7: Commit owned documentation**

```powershell
git commit -m "docs(agent): document grounded tool execution"
```
