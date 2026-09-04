# Session-scoped Agent Permissions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make ordinary Agent tools automatic, require one ephemeral external-folder grant per chat/session, and ask only for direct terminal deletion or an unrecognized executable.

**Architecture:** RuAgentBroker remains the single execution-policy authority. A new in-memory session directory grant module is consulted after hard invariants and before terminal risk classification; Host only bridges approvals and never persists or duplicates grants. Terminal policy uses versioned executable analyzers plus a PowerShell AST analyzer, while GUI and CLI expose one effective Agent profile and reason-specific approvals.

**Tech Stack:** .NET 10, C# 13, ASP.NET Core, WinUI 3, xUnit 2.9.2, Microsoft.PowerShell.SDK 7.6.5, PowerShell 7.6.5.

## Global Constraints

- Work in `D:\LLM`; inspect `git status --short --branch` before every task and preserve unrelated dirty work.
- Stage only files owned by the current task; never stage the whole tree.
- Project-root chat starts without workspace-trust or Full Access prompts.
- A directory grant is keyed by exact Agent session ID and canonical directory identity, covers descendants only, stays in memory, and is never shared with new or forked sessions.
- Closing/restarting Ru, Host, or Broker loses every grant; continuing the same session in the same process lifetime reuses it.
- Every non-terminal tool, including `files.delete` and `files.restore`, auto-allows after boundary and hard-invariant checks.
- Every recognized Git command and recognized package installation/restore command auto-allows.
- Direct terminal deletion and unrecognized executables require exact single-use approval even inside project root or an approved folder.
- Protected paths, malformed requests, stale tokens, identity mismatches, and hard timeout limits remain non-overridable denials.
- Broker is the only authority; Host/UI labels and model-provided safety fields are never trusted.
- Run heavyweight .NET suites sequentially.
- Baseline before this plan: 2,479 passing .NET tests and four unrelated Python failures from route-count/direct-route drift and missing ADR-0009 metadata.

---

## File structure

**Create:**

- `backend-dotnet/RuAgent.Broker/Policy/SessionDirectoryGrantRegistry.cs` — process-memory grants keyed by session.
- `backend-dotnet/RuAgent.Broker/Policy/Commands/PowerShellScriptAnalyzer.cs` — whole-script AST classification.
- `backend-dotnet/RuAgent.Broker.Tests/SessionDirectoryGrantRegistryTests.cs`.
- `backend-dotnet/RuAgent.Broker.Tests/PowerShellScriptAnalyzerTests.cs`.

**Modify:**

- `backend-dotnet/RuAgent.Protocol/Harness/TerminalContracts.cs` — explicit direct-delete class.
- `backend-dotnet/RuAgent.Broker/Policy/Commands/*.cs` — terminal classification.
- `backend-dotnet/RuAgent.Broker/Policy/PolicyContext.cs` and `PolicyContextFactory.cs` — trusted session/boundary facts.
- `backend-dotnet/RuAgent.Broker/Policy/PolicySnapshotEvaluator.cs` — one effective policy.
- `backend-dotnet/RuAgent.Broker/Policy/PendingApprovalStore.cs` and `Transport/AgentConnection.cs` — typed grants and approval ownership.
- `backend-dotnet/RuAgent.Protocol/AgentMessageTypes.cs` — internal session-grant revoke request/reply.
- `backend-dotnet/RuBackend/Harness/Broker/BrokerToolGateway.cs` and `Harness/AgentV1Service.cs` — thin Host bridge.
- `gui-winui3/Views/ChatView.xaml`, `ChatView.xaml.cs`, and Agent controller/client — simplified UX.
- CLI commands/help/tests and current ADR/architecture/operations docs.

---

### Task 1: Establish terminal risk vocabulary

**Files:**

- Modify: `backend-dotnet/RuAgent.Protocol/Harness/TerminalContracts.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Policy/Commands/CommandAnalyzerRegistry.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Policy/Commands/GitCommandAnalyzer.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Policy/Commands/PythonCommandAnalyzer.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Policy/Commands/DotnetCommandAnalyzer.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/GitCommandAnalyzerTests.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/PythonCommandAnalyzerTests.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/DotnetCommandAnalyzerTests.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs`

**Interfaces:**

- Consumes: `CommandAnalyzerRegistry.Analyze(string?, IReadOnlyList<string>)`.
- Produces: `TerminalOperationClass.DirectDelete`. `Unknown` asks; every other recognized class allows.

- [ ] **Step 1: Write failing analyzer tests**

~~~csharp
[Theory]
[InlineData("rm")]
[InlineData("del")]
[InlineData("erase")]
public void Direct_delete_executable_is_classified_for_approval(string executable)
{
    var result = new CommandAnalyzerRegistry().Analyze(executable + ".exe", ["target.txt"]);
    Assert.Equal(TerminalOperationClass.DirectDelete, result.OperationClass);
}

[Theory]
[InlineData("rm")]
[InlineData("clean")]
[InlineData("reset")]
public void Every_known_git_verb_remains_non_delete(string verb)
{
    var result = new GitCommandAnalyzer().Analyze("git.exe", [verb]);
    Assert.NotEqual(TerminalOperationClass.Unknown, result.OperationClass);
    Assert.NotEqual(TerminalOperationClass.DirectDelete, result.OperationClass);
}
~~~

Also assert `pip install`, `python -m pip install`, `dotnet restore`, and `dotnet add package` remain recognized non-delete operations.

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CommandAnalyzer|FullyQualifiedName~PolicyTests"
~~~

Expected: direct-delete assertions fail because the class is absent; policy still asks for recognized mutation classes.

- [ ] **Step 3: Add the minimal classification**

~~~csharp
public enum TerminalOperationClass
{
    Unknown = 0,
    ReadOnly = 1,
    Test = 2,
    Build = 3,
    Lint = 4,
    GitMutation = 5,
    ExternalMutation = 6,
    DirectDelete = 7,
}
~~~

Before family dispatch, map exact base names `rm`, `del`, and `erase` to `DirectDelete`. Keep every known Git verb in `GitMutation`/`ExternalMutation` and package operations in `ExternalMutation`.

Git remains a deliberate exception to direct-delete classification: `git rm`, `git clean`, `git reset`, and every other recognized Git verb are automatic. Package installation through `pip install`, `python -m pip install`, and recognized .NET package/restore commands is automatic.

- [ ] **Step 4: Run GREEN**

Run the Step 2 command. Expected: all selected tests pass.

- [ ] **Step 5: Commit**

~~~powershell
git add backend-dotnet/RuAgent.Protocol/Harness/TerminalContracts.cs backend-dotnet/RuAgent.Broker/Policy/Commands/CommandAnalyzerRegistry.cs backend-dotnet/RuAgent.Broker/Policy/Commands/GitCommandAnalyzer.cs backend-dotnet/RuAgent.Broker/Policy/Commands/PythonCommandAnalyzer.cs backend-dotnet/RuAgent.Broker/Policy/Commands/DotnetCommandAnalyzer.cs backend-dotnet/RuAgent.Broker.Tests/GitCommandAnalyzerTests.cs backend-dotnet/RuAgent.Broker.Tests/PythonCommandAnalyzerTests.cs backend-dotnet/RuAgent.Broker.Tests/DotnetCommandAnalyzerTests.cs backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs
git commit -m "feat(agent): classify terminal delete risk"
~~~

---

### Task 2: Add PowerShell AST classification

**Files:**

- Modify: `backend-dotnet/RuAgent.Broker/RuAgent.Broker.csproj`
- Create: `backend-dotnet/RuAgent.Broker/Policy/Commands/PowerShellScriptAnalyzer.cs`
- Create: `backend-dotnet/RuAgent.Broker.Tests/PowerShellScriptAnalyzerTests.cs`

**Interfaces:**

- Consumes: Task 1 command registry.
- Produces: `PowerShellScriptAnalyzer.Analyze(string) -> CommandAnalysis` for the complete script.

- [ ] **Step 1: Write RED AST tests**

~~~csharp
[Theory]
[InlineData("Remove-Item ./a.txt")]
[InlineData("del ./a.txt")]
[InlineData("erase ./a.txt")]
[InlineData("rm ./a.txt")]
public void Delete_command_asks(string script) =>
    Assert.Equal(TerminalOperationClass.DirectDelete, Analyze(script).OperationClass);

[Theory]
[InlineData("Invoke-Expression $command")]
[InlineData("& $executable --version")]
[InlineData("Get-Date; & 'unknown-tool.exe'")]
public void Dynamic_or_unknown_invocation_asks(string script) =>
    Assert.Equal(TerminalOperationClass.Unknown, Analyze(script).OperationClass);

[Fact]
public void Compound_script_is_atomic_when_later_statement_deletes() =>
    Assert.Equal(TerminalOperationClass.DirectDelete,
        Analyze("Get-Date; Remove-Item ./a.txt").OperationClass);
~~~

- [ ] **Step 2: Pin the parser package and run RED**

Add:

~~~xml
<PackageReference Include="Microsoft.PowerShell.SDK" Version="7.6.5" />
~~~

Run sequentially:

~~~powershell
dotnet restore backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~PowerShellScriptAnalyzerTests
~~~

Expected: compile fails because the analyzer type is absent.

- [ ] **Step 3: Implement whole-script analysis**

Use `System.Management.Automation.Language.Parser.ParseInput`. Parse errors, `Invoke-Expression`, dynamic command names, unresolved call operators, and unknown static executables return `Unknown`. `Remove-Item` and aliases return `DirectDelete`. Statically recognized external commands use `CommandAnalyzerRegistry`. Combine every command before execution; one unsafe command makes the whole script ask.

~~~csharp
public CommandAnalysis Analyze(string script)
{
    var ast = Parser.ParseInput(script, out _, out var errors);
    if (errors.Length != 0) return Unknown("powershell_parse_error");
    var commands = ast.FindAll(node => node is CommandAst, true).Cast<CommandAst>();
    return Combine(commands.Select(AnalyzeCommand));
}
~~~

- [ ] **Step 4: Run Broker tests**

~~~powershell
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --no-restore
~~~

Expected: all Broker tests pass.

- [ ] **Step 5: Commit**

~~~powershell
git add backend-dotnet/RuAgent.Broker/RuAgent.Broker.csproj backend-dotnet/RuAgent.Broker/Policy/Commands/PowerShellScriptAnalyzer.cs backend-dotnet/RuAgent.Broker.Tests/PowerShellScriptAnalyzerTests.cs
git commit -m "feat(agent): analyze PowerShell terminal risk"
~~~

---

### Task 3: Add ephemeral session directory grants

**Files:**

- Create: `backend-dotnet/RuAgent.Broker/Policy/SessionDirectoryGrantRegistry.cs`
- Create: `backend-dotnet/RuAgent.Broker.Tests/SessionDirectoryGrantRegistryTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Runtime/BrokerSession.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/BrokerSessionTests.cs`

**Interfaces:**

- Produces: `ISessionDirectoryGrantRegistry.Contains`, `Add`, and `RevokeSession`.
- Produces: one `BrokerSession.DirectoryGrants` registry for the Broker process lifetime.

- [ ] **Step 1: Write RED ownership tests**

~~~csharp
[Fact]
public void Grant_is_reusable_only_by_same_session_and_descendants()
{
    var grants = new SessionDirectoryGrantRegistry();
    grants.Add("session-a", CanonicalDirectoryIdentity.CreateExisting(@"D:\shared"));
    Assert.True(grants.Contains("session-a", @"D:\shared\child\x.txt"));
    Assert.False(grants.Contains("session-b", @"D:\shared\child\x.txt"));
    Assert.False(grants.Contains("session-a", @"D:\shared-sibling\x.txt"));
}

[Fact]
public void Closing_session_revokes_only_its_grants() { }
~~~

Add junction/final-path, case-insensitive Windows path, sibling-prefix, new-session, and forked-session cases.

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SessionDirectoryGrantRegistryTests|FullyQualifiedName~BrokerSessionTests"
~~~

Expected: compile fails because registry types do not exist.

- [ ] **Step 3: Implement the process-memory registry**

~~~csharp
public interface ISessionDirectoryGrantRegistry
{
    bool Contains(string sessionId, string canonicalTarget);
    void Add(string sessionId, CanonicalDirectoryIdentity directory);
    void RevokeSession(string sessionId);
}

public sealed record CanonicalDirectoryIdentity(string CanonicalPath)
{
    public static CanonicalDirectoryIdentity CreateExisting(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        return new CanonicalDirectoryIdentity(FinalPathResolver.Resolve(path));
    }
}
~~~

Use a lock and per-session sets. Accept only final canonical existing directories. Compare path components case-insensitively; do not use raw prefix matching. Reset the whole registry when the owning Broker transport session closes; do not revoke it when one run lease closes because a later run in the same Agent session must reuse the grant. Add no serializer, database, event, or snapshot representation.

- [ ] **Step 4: Run GREEN and commit**

~~~powershell
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SessionDirectoryGrantRegistryTests|FullyQualifiedName~BrokerSessionTests"
git add backend-dotnet/RuAgent.Broker/Policy/SessionDirectoryGrantRegistry.cs backend-dotnet/RuAgent.Broker/Runtime/BrokerSession.cs backend-dotnet/RuAgent.Broker.Tests/SessionDirectoryGrantRegistryTests.cs backend-dotnet/RuAgent.Broker.Tests/BrokerSessionTests.cs
git commit -m "feat(agent): add ephemeral session directory grants"
~~~

---

### Task 4: Replace preset fallbacks with boundary-first policy

**Files:**

- Modify: `backend-dotnet/RuAgent.Broker/Policy/PolicyContext.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Policy/PolicyContextFactory.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Policy/PolicySnapshotEvaluator.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/Terminal/TerminalManifest.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/FileDiscoveryAdapterTests.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/TerminalAdapterTests.cs`

**Interfaces:**

- Consumes: Tasks 1-3.
- Produces: trusted `PolicyContext.SessionId`, nullable `string? BoundaryPath`, and `CommandAnalysis? TerminalAnalysis` plus exact decision codes.

- [ ] **Step 1: Add RED policy matrix**

~~~csharp
[Theory]
[InlineData("files.read")]
[InlineData("files.write")]
[InlineData("files.edit")]
[InlineData("files.delete")]
[InlineData("files.restore")]
public void Every_file_tool_allows_inside_authorized_boundary(string toolId) { }

[Fact] public void External_target_without_grant_asks_external_directory_required() { }
[Fact] public void Terminal_delete_asks_inside_granted_external_directory() { }
[Fact] public void Unknown_executable_asks_inside_project_root() { }
[Fact] public void Git_push_and_pip_install_allow_inside_boundary() { }
~~~

Assert exact codes `external_directory_required`, `terminal_delete_requires_approval`, and `terminal_unknown_executable`.

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PolicyTests|FullyQualifiedName~FileDiscoveryAdapterTests|FullyQualifiedName~TerminalAdapterTests"
~~~

Expected: external terminal cwd denies too early, file delete asks, and Full Access allows unknown/delete terminal calls.

- [ ] **Step 3: Build trusted policy context**

Derive session ID from `BrokerRunLease.SessionId`. Permit explicit absolute external terminal cwd to reach policy evaluation while keeping relative traversal invalid. Resolve `terminal.run` with `CommandIntentResolver` and `terminal.shell` with `PowerShellScriptAnalyzer`. Store only canonical Broker-derived facts.

- [ ] **Step 4: Implement one effective evaluator**

~~~csharp
private PolicyEvaluation EvaluateEffectivePolicy(PolicyContext context)
{
    if (context.BoundaryPath is { } boundary &&
        !IsInsideRoot(context) &&
        !_directoryGrants.Contains(context.SessionId, boundary))
        return Ask("external_directory_required");

    if (context.TerminalAnalysis?.OperationClass == TerminalOperationClass.DirectDelete)
        return Ask("terminal_delete_requires_approval");

    if (context.TerminalAnalysis?.OperationClass == TerminalOperationClass.Unknown)
        return Ask("terminal_unknown_executable");

    return Allow("session_policy_allow");
}
~~~

Evaluate hard invariants and protected paths first. Both compatibility preset values call this same path. Remove manifest-level unconditional approval for file mutation and shell; contextual policy owns approval.

- [ ] **Step 5: Run GREEN and commit**

~~~powershell
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --no-restore
git add backend-dotnet/RuAgent.Broker/Policy/PolicyContext.cs backend-dotnet/RuAgent.Broker/Policy/PolicyContextFactory.cs backend-dotnet/RuAgent.Broker/Policy/PolicySnapshotEvaluator.cs backend-dotnet/RuAgent.Broker/Tools/Terminal/TerminalManifest.cs backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs backend-dotnet/RuAgent.Broker.Tests/FileDiscoveryAdapterTests.cs backend-dotnet/RuAgent.Broker.Tests/TerminalAdapterTests.cs
git commit -m "feat(agent): enforce session boundary policy"
~~~

---

### Task 5: Make Broker approval own the directory grant

**Files:**

- Modify: `backend-dotnet/RuAgent.Protocol/AgentMessageTypes.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Policy/PendingApprovalStore.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/PendingApprovalStoreTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/PendingApprovalPreviewTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs`

**Interfaces:**

- Produces: `V2ApprovalBinding.ApprovalKind` = `external_directory` or `terminal_risk` and canonical `BoundaryDirectory`.
- Produces: Broker command `allow_session_directory`; terminal approval remains one exact authorization token.
- Produces: internal `session.grants.revoke` / `session.grants.revoked` messages bound to the active Broker connection.

- [ ] **Step 1: Add RED binding tests**

~~~csharp
[Fact] public void External_approval_binds_exact_session_directory_not_tool_id() { }
[Fact] public void Approved_directory_allows_different_tool_in_same_session() { }
[Fact] public void Terminal_approval_creates_no_reusable_grant() { }
[Fact] public void Stale_or_cross_session_approval_cannot_add_grant() { }
[Fact] public void Revoke_message_removes_only_the_named_agent_session_grants() { }
~~~

Preview must contain only safe approval kind and bounded canonical directory, never script/raw argv.

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PendingApproval|FullyQualifiedName~BrokerHostTests"
~~~

Expected: pending bindings cannot distinguish directory and terminal approvals.

- [ ] **Step 3: Extend approval flow**

Derive approval kind from the evaluator reason. On `allow_session_directory`, atomically consume and revalidate run/session/snapshot/intent, add the canonical boundary to `BrokerSession.DirectoryGrants`, then issue the one-time token that resumes the original operation. Terminal approval issues only the token. Emit no reusable policy candidate.

Add `AgentMessageTypes.SessionGrantsRevoke` and `SessionGrantsRevoked`. The handler requires the active Broker transport session, reads one bounded Agent session ID, invokes `DirectoryGrants.RevokeSession`, and returns a safe status without exposing directory paths.

- [ ] **Step 4: Run GREEN and commit**

~~~powershell
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --no-restore
git add backend-dotnet/RuAgent.Protocol/AgentMessageTypes.cs backend-dotnet/RuAgent.Broker/Policy/PendingApprovalStore.cs backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs backend-dotnet/RuAgent.Broker.Tests/PendingApprovalStoreTests.cs backend-dotnet/RuAgent.Broker.Tests/PendingApprovalPreviewTests.cs backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs
git commit -m "feat(agent): grant external folders per session"
~~~

---

### Task 6: Reduce Host to a thin approval bridge

**Files:**

- Modify: `backend-dotnet/RuBackend/Harness/Broker/BrokerToolGateway.cs`
- Modify: `backend-dotnet/RuBackend/Harness/AgentV1Service.cs`
- Modify: `backend-dotnet/RuBackend/Harness/HarnessRunService.cs`
- Modify: `backend-dotnet/RuBackend/Harness/HarnessSessionDeletionCoordinator.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Harness/AgentApprovalEndpointTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Harness/AgentApprovalWorkflowTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Harness/AgentSessionPermissionEndpointTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Harness/Phase1RemediationTests.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Harness/HarnessSessionDeletionCoordinatorTests.cs`

**Interfaces:**

- Consumes: typed Broker reasons and `allow_session_directory`.
- Produces: endpoint approval chosen from pending kind, never caller-selected persistence scope.

- [ ] **Step 1: Add RED integration tests**

~~~csharp
[Fact] public async Task External_folder_approval_never_writes_policy_rule() { }
[Fact] public async Task Same_session_second_tool_reuses_broker_grant() { }
[Fact] public async Task New_and_forked_sessions_ask_again() { }
[Fact] public async Task Deleting_session_revokes_its_broker_directory_grants() { }

[Theory]
[InlineData("terminal_delete_requires_approval")]
[InlineData("terminal_unknown_executable")]
public async Task Terminal_reason_reaches_public_api(string reason) { }
~~~

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentApproval|FullyQualifiedName~AgentSessionPermission|FullyQualifiedName~Phase1Remediation"
~~~

Expected: Host attempts durable run/project rules and collapses terminal reasons.

- [ ] **Step 3: Implement the thin bridge**

Preserve all three reason codes. Send `allow_session_directory` only for `external_directory_required` and `allow_once` for terminal risk. Remove `HarnessApprovalService.ProcessAsync` and durable rule creation from external-folder approval. Reject run/project/global scope for these pending kinds. Add `BrokerToolGateway.RevokeSessionGrantsAsync(string sessionId, CancellationToken)` and call it from `HarnessSessionDeletionCoordinator` before durable session deletion; a Broker-unavailable result is safe because grants cannot execute without that Broker, but any live-Broker rejection fails the delete closed. Keep wire preset parsing for compatibility, but map both values to the same effective policy. Project root needs no trust resolution.

- [ ] **Step 4: Run GREEN and commit**

~~~powershell
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentApproval|FullyQualifiedName~AgentSessionPermission|FullyQualifiedName~Phase1Remediation"
git add backend-dotnet/RuBackend/Harness/Broker/BrokerToolGateway.cs backend-dotnet/RuBackend/Harness/AgentV1Service.cs backend-dotnet/RuBackend/Harness/HarnessRunService.cs backend-dotnet/RuBackend/Harness/HarnessSessionDeletionCoordinator.cs backend-dotnet/RuBackend.Tests/Harness/AgentApprovalEndpointTests.cs backend-dotnet/RuBackend.Tests/Harness/AgentApprovalWorkflowTests.cs backend-dotnet/RuBackend.Tests/Harness/AgentSessionPermissionEndpointTests.cs backend-dotnet/RuBackend.Tests/Harness/Phase1RemediationTests.cs backend-dotnet/RuBackend.Tests/Harness/HarnessSessionDeletionCoordinatorTests.cs
git commit -m "feat(agent): bridge session-scoped approvals"
~~~

---

### Task 7: Simplify GUI and CLI permission UX

**Files:**

- Modify: `gui-winui3/Views/ChatView.xaml`
- Modify: `gui-winui3/Views/ChatView.xaml.cs`
- Modify: `gui-winui3/Services/AgentRunController.cs`
- Modify: `gui-winui3/Services/AgentRunClientAdapter.cs`
- Modify: `gui-winui3.Tests/AgentApprovalUiContractTests.cs`
- Modify: `gui-winui3.Tests/AgentSessionUiSourceContractTests.cs`
- Modify: `gui-winui3.Tests/AgentRunControllerTests.cs`
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.cs`
- Modify: `backend-dotnet/RuAgent.Cli/README.md`
- Modify: `backend-dotnet/RuAgent.Cli.Tests/ApprovalCommandTests.cs`
- Modify: `backend-dotnet/RuAgent.Cli.Tests/AgentCliParserTests.cs`

**Interfaces:**

- Consumes: server-selected pending approval kind.
- Produces: `AgentRunController.ApproveAsync()` without a caller-selected persistence scope.

- [ ] **Step 1: Add RED UI/CLI tests**

~~~csharp
[Fact]
public void Chat_has_no_trust_preset_or_scope_controls()
{
    var xaml = ReadChatXaml();
    Assert.DoesNotContain("AgentTrustPanel", xaml);
    Assert.DoesNotContain("AgentPresetBox", xaml);
    Assert.DoesNotContain("AgentApprovalScopeBox", xaml);
    Assert.Contains("AgentApprovalPanel", xaml);
}
~~~

Add assertions for three reason-specific messages. Add CLI tests proving approval needs no run/project scope and JSON mode cannot bypass a decision.

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentApproval|FullyQualifiedName~AgentSessionUi|FullyQualifiedName~AgentRunController"
dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ApprovalCommandTests|FullyQualifiedName~AgentCliParserTests"
~~~

Expected: obsolete controls and scoped approval API remain.

- [ ] **Step 3: Remove obsolete authority controls**

Remove trust, preset, and approval-scope controls/state/handlers. Start runs with the compatibility value required by the endpoint but do not expose it as authority. Render bounded copy:

~~~text
external_directory_required: Ru cần dùng thư mục {canonical folder} trong đoạn chat này, cho đến khi ứng dụng đóng.
terminal_delete_requires_approval: Lệnh terminal này sẽ xóa file.
terminal_unknown_executable: Ru chưa nhận diện được executable của lệnh terminal này.
~~~

Never show raw script, argv, output, or secrets.

- [ ] **Step 4: Simplify API calls and CLI**

Expose parameterless `ApproveAsync()`. Let Host/Broker choose directory grant versus one-time terminal token from the pending binding. Remove reusable run/project scope options from the normal CLI approval command and help.

- [ ] **Step 5: Run tests/builds and commit**

~~~powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --no-restore
dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Release --no-restore
dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64 --no-restore
dotnet build gui-winui3.Harness/RuApp.Harness.csproj -c Release --no-restore
git add gui-winui3/Views/ChatView.xaml gui-winui3/Views/ChatView.xaml.cs gui-winui3/Services/AgentRunController.cs gui-winui3/Services/AgentRunClientAdapter.cs gui-winui3.Tests/AgentApprovalUiContractTests.cs gui-winui3.Tests/AgentSessionUiSourceContractTests.cs gui-winui3.Tests/AgentRunControllerTests.cs backend-dotnet/RuAgent.Cli/AgentCommands.cs backend-dotnet/RuAgent.Cli/README.md backend-dotnet/RuAgent.Cli.Tests/ApprovalCommandTests.cs backend-dotnet/RuAgent.Cli.Tests/AgentCliParserTests.cs
git commit -m "feat(agent): simplify session permission UX"
~~~

---

### Task 8: Update current docs and run final gates

**Files:**

- Modify: `docs/adr/0007-mode-capability-profiles.md`
- Modify: `docs/adr/0009-broker-execution-authority.md`
- Modify: `docs/architecture.md`
- Modify: `docs/operations/agent-tools.md`
- Modify: `README.md`
- Modify: `docs/README.md` only when its current-state prose describes old behavior.
- Test: `tests/test_current_documentation.py`

**Interfaces:**

- Consumes: exact shipped behavior and reason codes.
- Produces: one current documentation story; dated historical plans/specs remain unchanged.

- [ ] **Step 1: Add RED documentation assertions**

Assert current docs contain `session-scoped`, `external_directory_required`, `terminal_delete_requires_approval`, and `terminal_unknown_executable`, and do not claim external grants are durable run/project rules or Full Access allows every well-formed call.

- [ ] **Step 2: Run RED**

~~~powershell
& .\.venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q
~~~

Expected: permission assertions and existing ADR-0009 metadata check fail.

- [ ] **Step 3: Update current-state documents**

Add valid metadata to ADR-0009. Mark ADR-0007 superseded while retaining decision history. Update architecture, operations, README, and the docs index only where current behavior is stated. Do not rewrite historical plans/specs.

- [ ] **Step 4: Run focused GREEN**

~~~powershell
& .\.venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q
dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj -c Release --no-restore
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentApproval|FullyQualifiedName~AgentSessionPermission|FullyQualifiedName~Phase1Remediation"
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentApproval|FullyQualifiedName~AgentSessionUi|FullyQualifiedName~AgentRunController"
dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ApprovalCommandTests|FullyQualifiedName~AgentCliParserTests"
~~~

Expected: every selected permission/documentation test passes.

- [ ] **Step 5: Run repository-wide sequential verification**

~~~powershell
& .\scripts\test_dotnet.ps1 -Configuration Release -NoRestore
& .\.venv\Scripts\python.exe -m ruff check src tests scripts tools
& .\.venv\Scripts\python.exe -m mypy src
& .\.venv\Scripts\python.exe -m pip check
& .\.venv\Scripts\python.exe -m pytest -q
dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64 --no-restore
dotnet build gui-winui3.Harness/RuApp.Harness.csproj -c Release --no-restore
git diff --check
~~~

Expected: .NET tests, Ruff, Mypy, pip check, and builds pass. Compare Python results with the recorded four-test baseline; introduce no new failure. If route-count work has landed, require the full Python suite to pass.

- [ ] **Step 6: Perform manual GUI acceptance only with explicit authorization**

Verify: root chat starts silently; external folder asks once; same session reuses it; new session asks; restart asks; `files.delete` is automatic; direct terminal deletion asks; Git/package install is automatic; unknown executable asks.

- [ ] **Step 7: Commit docs**

~~~powershell
git add README.md docs/README.md docs/adr/0007-mode-capability-profiles.md docs/adr/0009-broker-execution-authority.md docs/architecture.md docs/operations/agent-tools.md tests/test_current_documentation.py
git commit -m "docs: publish session-scoped agent permissions"
~~~

Do not push until the user explicitly requests it.
