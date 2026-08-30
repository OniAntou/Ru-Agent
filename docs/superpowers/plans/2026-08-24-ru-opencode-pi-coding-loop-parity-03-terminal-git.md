# Ru Coding-Loop Parity Phase 3: Terminal, Local Git, and Reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Status (2026-08-29): partially shipped historical checkpoint.** Terminal streaming, command analysis, cancellation, and reconciliation are implemented on `main` and are model-visible through the Worker v2 surface. Managed Git commits (Task 6) and real-model acceptance remain future work; use `docs/operations/agent-tools.md` and `docs/README.md` for the current boundary.

**Goal:** Complete the headless coding loop with bounded streaming terminal execution, recognized test/build/lint commands, local Git mutation and managed commits, full process-tree cancellation, and explicit crash reconciliation. The shipped portion is terminal execution, cancellation, and reconciliation; managed Git remains open.

**Architecture:** Broker resolves/analyzes commands and remains final execution authority; a dedicated streaming process runner owns one Job Object per call; Core persists dispatch/receipt boundaries and ambiguous outcomes; Host stores run-attributed Git/workspace snapshots; CLI renders the same durable/transient events and supplies explicit reconcile input.

**Tech Stack:** .NET 10, xUnit, PowerShell, Windows Job Objects, System.Threading.Channels, Broker protocol v2, Git CLI, dotnet, Python/pytest/Ruff/Mypy, ripgrep, temporary Git repositories.

---

## Checkpoint outcome

The shipped checkpoint covers the headless terminal/file path, streaming output, cancellation, and reconciliation. Local Git reads/mutations/managed commit and real-model acceptance are not yet shipped; the Desktop Agent cutover is recorded separately. Overall OpenCode/Pi parity is not claimed.

## Task 1: Finalize terminal schemas and canonical command intent

**Files:**

- Modify `backend-dotnet/RuAgent.Protocol/AgentModelToolContractCatalog.cs`.
- Create `backend-dotnet/RuAgent.Protocol/Harness/TerminalContracts.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Tools/Terminal/TerminalManifest.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Terminal/CanonicalCommandIntent.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Terminal/CommandIntentResolver.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/TerminalAdapterTests.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/CommandIntentResolverTests.cs`.

- [ ] Write RED schema tests for exact inputs:

```text
terminal.run:
  executable: non-empty string
  arguments: bounded string array
  working_directory: path, default "."
  timeout_ms: integer, default 120000, hard max 1800000
  network_intent: boolean, default false

terminal.shell:
  script: bounded PowerShell text
  working_directory: path, default "."
  timeout_ms: integer, default 120000, hard max 1800000
  network_intent: boolean, default false
```

No model-provided environment field exists.

- [ ] Add RED canonicalization tests for executable resolution through a sanitized PATH, argv preservation without shell joining, canonical cwd classification, recognized/declarative network intent, operation class, timeout class, and stable canonical intent hash.
- [ ] Add RED invariant tests rejecting empty argv entries containing NUL, interactive/PTY flags, password-prompt forms, background-daemon forms, invalid cwd, timeout over 30 minutes, and shell runtimes other than explicit PowerShell for `terminal.shell`.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ToolCatalog|FullyQualifiedName~Terminal|FullyQualifiedName~CommandIntentResolver"
```

Expected RED: new contracts/resolver absent and old `network`/working-directory naming assertions fail.

- [ ] Implement structural resolution with `ProcessStartInfo.ArgumentList`; never concatenate `terminal.run` argv into shell text.
- [ ] Keep `network_intent` in policy/audit only. Do not emit copy implying sockets are blocked.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(agent): canonicalize terminal command intent`.

## Task 2: Build a useful non-secret Agent command environment

**Files:**

- Create `backend-dotnet/RuAgent.Broker/Tools/Terminal/AgentCommandEnvironment.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Terminal/SecretEnvironmentNamePolicy.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/AgentCommandEnvironmentTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Tools/Terminal/TerminalAdapter.cs` after focused tests exist.

- [ ] Write RED allow tests for required Windows/developer values: SystemRoot/WINDIR, TEMP/TMP, USERPROFILE/HOME, LOCALAPPDATA, APPDATA, ProgramData, ProgramFiles variants, PATH/PATHEXT/COMSPEC, processor values, Git config roots, DOTNET/SDK paths, NuGet cache roots, Python/virtual-environment paths, and package cache roots when present.
- [ ] Write RED strip tests case-insensitively for Ru/Worker/Broker/Desktop bearer values and names containing token, secret, password, passwd, api key, provider key, credential, connection string, private key, access key, refresh key, or auth key.
- [ ] Add RED tests proving values are copied from a captured parent snapshot, not lazily read after execution begins; model input cannot add/override environment entries; diagnostics contain names/counts only and never values.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~AgentCommandEnvironment"
```

Expected RED: environment service absent.

- [ ] Implement an explicit allowlist plus secret-name deny filter. Add fixed non-interactive child values such as `GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=Never`, and UTF-8 process I/O settings; never overwrite the parent HOME/USERPROFILE variables in the parent process. Do not reuse the smaller Public Host bootstrap environment as the Agent command environment.
- [ ] Seed the existing known-secret streaming redactor with stripped Ru-owned values before discarding them from the child environment.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(agent): provide sanitized command environment`.

## Task 3: Add versioned command analyzers and Standard decisions

**Files:**

- Create `backend-dotnet/RuAgent.Broker/Policy/Commands/ICommandAnalyzer.cs`.
- Create `backend-dotnet/RuAgent.Broker/Policy/Commands/CommandAnalysis.cs`.
- Create `backend-dotnet/RuAgent.Broker/Policy/Commands/GitCommandAnalyzer.cs`.
- Create `backend-dotnet/RuAgent.Broker/Policy/Commands/DotnetCommandAnalyzer.cs`.
- Create `backend-dotnet/RuAgent.Broker/Policy/Commands/PythonCommandAnalyzer.cs`.
- Create `backend-dotnet/RuAgent.Broker/Policy/Commands/RipgrepCommandAnalyzer.cs`.
- Create `backend-dotnet/RuAgent.Broker/Policy/Commands/CommandAnalyzerRegistry.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/GitCommandAnalyzerTests.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/DotnetCommandAnalyzerTests.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/PythonCommandAnalyzerTests.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/RipgrepCommandAnalyzerTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Policy/PolicySnapshotEvaluator.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs`.

- [ ] Define RED analyzer matrices with exact expected `ReadOnly|Test|Build|Lint|GitMutation|ExternalMutation|Unknown`, network intent, and reason code.
- [ ] Git allow matrix: `status`, `diff`, `log`, `show`, `rev-parse`, `branch --show-current`, and `ls-files` without mutation flags. Git mutation/commit asks. Push/fetch/pull/remote mutation are external/network and not a first-class workflow.
- [ ] .NET allow matrix: `dotnet test`, `dotnet build`, and non-mutating diagnostic forms. `restore`, tool/package install, publish/deploy, run, and unknown verbs ask.
- [ ] Python allow matrix: `pytest`, `python -m pytest`, `ruff check`, `python -m ruff check`, `mypy`, and `python -m mypy` under bounded arguments. Arbitrary scripts, pip/install, module execution, and unknown flags ask.
- [ ] Ripgrep allow matrix: search/list-files forms; reject replacement/preprocessor/unknown executable forms to Ask.
- [ ] Add RED `terminal.shell` tests: Standard asks unless an exact reusable rule matches; no regex parser claims a script safe. Unknown executable forms ask. Full Access allows well-formed calls, except a timeout over 15 minutes still needs exact approval and the 30-minute cap remains absolute.
- [ ] Run RED analyzer/policy tests.
- [ ] Implement analyzers as small pure classes with `Version = "1"`; invalid parsing returns typed Unknown/Ask, never Allow.
- [ ] Run GREEN. Expected: all matrices pass.
- [ ] Commit: `feat(agent): classify recognized developer commands`.

## Task 4: Stream bounded stdout/stderr and own each process tree

**Files:**

- Create `backend-dotnet/RuAgent.Protocol/Harness/BrokerToolOutputDeltaContracts.cs`.
- Modify `backend-dotnet/RuAgent.Protocol/AgentMessageTypes.cs` and codec tests.
- Create `backend-dotnet/RuAgent.Broker/Tools/Terminal/IProcessTreeController.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Terminal/WindowsJobProcessTree.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Terminal/StreamingProcessRunner.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Terminal/HeadTailOutputBuffer.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/StreamingProcessRunnerTests.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/HeadTailOutputBufferTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Tools/ProcessTreeRunner.cs` into a v1 compatibility adapter, then remove it in phase 4 when unused.
- Modify `backend-dotnet/RuBackend/Harness/Broker/HarnessToolGateway.cs` and SSE live hub.

- [ ] Write RED protocol tests: `tool.output.delta` is v2-only, bound to run/tool/execution, has monotonic frame sequence, `stdout|stderr`, bounded bytes, redaction/truncation metadata, and cannot masquerade as final receipt.
- [ ] Add RED gateway mapping tests proving the Broker wire delta maps to Core `ToolOutputChunk` and then to a public transient SSE frame without Core referencing `RuAgent.Protocol` or persisting the transient bytes.
- [ ] Write RED runner tests for separate concurrent stdout/stderr drains, monotonic merged frame sequence, known-secret split across chunks, invalid UTF-8 replacement policy, head/tail retention, truncation counters, and continued drain after retention cap.
- [ ] Add slow-consumer RED test: bounded Broker→Host→SSE channels may drop transient deltas with a safe dropped-frame count, but process drain and final receipt complete.
- [ ] Add real Windows RED cancellation/timeout tests that launch a child which launches a grandchild; closing the per-call Job Object terminates both. Assert `KILL_ON_JOB_CLOSE`, no silent/breakaway allowance, and that a parent which exits while leaving a child cannot create a surviving background daemon. Test normal exit, cancellation race, 120-second default via fake time where possible, and hard 30-minute validation without waiting.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~StreamingProcessRunner|FullyQualifiedName~HeadTailOutput|FullyQualifiedName~AgentEnvelope"
```

Expected RED: streaming protocol/runner absent.

- [ ] Implement one process tree per call and no persistent shell state. Final receipt includes exit code, duration, canonical cwd, per-stream byte/truncation counts, and bounded head/tail private payload references.
- [ ] Forward redacted transient frames only; do not persist every delta as a durable event.
- [ ] Run GREEN. Expected: zero failures and no orphan test process.
- [ ] Commit: `feat(agent): stream bounded terminal output`.

## Task 5: Execute terminal calls through the durable Core boundary

**Files:**

- Refactor `backend-dotnet/RuAgent.Broker/Tools/Terminal/TerminalAdapter.cs`.
- Modify `backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs`.
- Modify `backend-dotnet/RuBackend/Harness/Broker/HarnessToolGateway.cs`.
- Modify `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/TerminalAdapterTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/ToolExecutionServiceTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/HarnessTerminalIntegrationTests.cs`.

- [ ] Add RED end-to-end tests for an allowed `rg`, `dotnet test`, `dotnet build`, and pytest command; approval-required unknown executable and shell; denial on malformed/interactive/background commands; Full Access fallback; protected/outside cwd decisions.
- [ ] Prove canonical intent hash binds resolved executable/argv/cwd/operation/network/timeout and changes invalidate approval. Re-resolve executable/cwd at preparation and execution.
- [ ] Prove Core commits `tool.execution_requested` before process start; final receipt before next model step; cancellation posts to Worker and Broker; timeout/cancel event distinguishes `cancelled_side_effects_possible` when applicable.
- [ ] Prove Standard timeout ≤15 minutes follows command decision; >15 and ≤30 minutes asks exactly; >30 minutes denies before adapter. Approval waiting consumes no budget.
- [ ] Run RED across Broker/Core/Backend focused tests sequentially.
- [ ] Implement integration without sending commands/output into safe events, Chat history, memory, TTS, VTS, or diagnostic JSONL.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(agent): execute terminal through durable core`.

## Task 6: Capture Git/worktree state and attribute run changes

**Files:**

- Create `backend-dotnet/RuAgent.Broker/Tools/Git/GitRepositoryLocator.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Git/GitWorktreeSnapshot.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Git/GitStatusParser.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Git/GitChangeAttributor.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/GitStatusParserTests.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/GitChangeAttributorTests.cs`.
- Create `backend-dotnet/RuBackend/Harness/Git/SqliteRunGitStateStore.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/RunGitStateStoreTests.cs`.

- [ ] Write RED parser tests for porcelain v2 including spaces, renames, copies, submodules, staged/unstaged/untracked, detached HEAD, no repository, and bounded output.
- [ ] Add RED temp-repo tests capturing start HEAD/branch/index hash/status and observed file hashes. A non-empty index at run start sets `ManagedCommitEligible=false` permanently for that run.
- [ ] Add RED attribution tests: only file-tool mutations and verified before/after terminal snapshot deltas are candidates; unrelated pre-existing or concurrent dirty paths remain excluded; ambiguous terminal-created changes are flagged, not attributed silently.
- [ ] Add RED durable rehydration tests: Broker restart receives the Host-stored bounded start snapshot and attributed-path set in a refreshed lease; raw diffs/content stay encrypted/private.
- [ ] Run RED across Broker and Backend focused tests.
- [ ] Implement Git reads through structured `terminal.run` execution with fixed `--no-optional-locks` where safe and bounded parsing. Never infer clean from a failed Git command.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(agent): track run attributed git changes`.

## Task 7: Add the managed local-commit workflow

**Files:**

- Create `backend-dotnet/RuAgent.Broker/Tools/Git/ManagedCommitPlan.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Git/ManagedCommitService.cs`.
- Create `backend-dotnet/RuAgent.Broker/Tools/Git/TemporaryGitIndex.cs`.
- Create `backend-dotnet/RuAgent.Broker.Tests/ManagedCommitServiceTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/GitCommandAnalyzerTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs`.
- Modify `backend-dotnet/RuAgent.Broker.Tests/PendingApprovalPreviewTests.cs`.

- [ ] Build RED disposable-repo fixtures with: clean repo; unrelated unstaged file; non-empty start index; path dirty before run; concurrent index change; rename; no branch; commit hook failure.
- [ ] Add RED preview tests: construct a temporary index from HEAD, include only run-attributed paths, attempt an exact Agent-only patch for a dirty-at-start path, exclude it if proof fails, and produce an exact staged-path/diff hash for approval without changing the real index.
- [ ] Add RED execution tests: after exact approval, revalidate HEAD/branch/index/worktree/preview hash, require current real index empty, stage only approved paths, verify staged diff matches, use a non-interactive editor/signing configuration, commit, verify HEAD advanced exactly once with the approved diff hash, and leave unrelated dirty files untouched.
- [ ] Add RED failure tests proving pre-existing/concurrent index changes disable/abort managed commit; no implicit reset, clean, force checkout, whole-tree stage, push, or PR; a failed commit restores the exact pre-operation index bytes only if its hash still matches the service-owned state, otherwise enters reconciliation. A hook that mutates index/worktree or leaves an ambiguous commit outcome is never reset automatically and enters reconciliation with a fresh snapshot.
- [ ] Add RED policy tests: Git commit/mutation asks in Standard; Full Access well-formed arbitrary shell can bypass managed workflow and UI/docs warn that this is outside the managed safety path.
- [ ] Run RED:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --nologo --filter "FullyQualifiedName~ManagedCommit|FullyQualifiedName~GitCommandAnalyzer"
```

Expected RED: managed commit services absent.

- [ ] Implement the managed interception for `terminal.run git commit`; do not add a separate model-visible Git tool.
- [ ] Run GREEN. Expected: all disposable repos preserve unrelated staged/unstaged changes.
- [ ] Commit: `feat(agent): add dirty safe local commit workflow`.

## Task 8: Implement startup/cancellation reconciliation and context limits

**Files:**

- Create `backend-dotnet/RuAgent.Harness.Core/Reconciliation/ReconciliationCommand.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Reconciliation/ReconciliationState.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Context/ModelContextSizeEstimator.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core/Context/ContinuationSummaryBuilder.cs`.
- Create `backend-dotnet/RuBackend/Harness/Reconciliation/HarnessRecoveryScanner.cs`.
- Create `backend-dotnet/RuBackend/Harness/Reconciliation/WorkspaceStateSnapshotService.cs`.
- Modify `backend-dotnet/RuAgent.Harness.Core/AgentRunCoordinator.cs`.
- Modify `backend-dotnet/RuBackend/Harness/AgentV1Service.cs`.
- Modify `backend-dotnet/RuBackend/Program.cs` for the recovery hosted service registration.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/ReconciliationStateTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/HarnessRecoveryScannerTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/AgentReconcileEndpointTests.cs`.
- Modify `backend-dotnet/RuAgent.Harness.Core.Tests/AgentBudgetTests.cs`.
- Create `backend-dotnet/RuAgent.Harness.Core.Tests/ModelContextLimitTests.cs`.

- [ ] Add RED startup matrix: crash before dispatch → safe failure/no effect; read-only after dispatch → at most two same-ID retries; deterministic file mutation → compare hashes; terminal/Git after dispatch without durable receipt → `ReconciliationRequired`; no startup path reissues old mutation execution ID.
- [ ] Add RED endpoint tests for displayed canonical intent plus fresh workspace/Git snapshot hash and `Applied|NotApplied|UnknownContinue`. Mismatched/stale snapshot hash returns `reconcile_stale`.
- [ ] Add RED model-feedback tests: Applied/NotApplied produce typed outcomes; UnknownContinue produces `outcome_unknown` and Broker denies mutation until one durable read-only inspection receipt; then normal evaluation resumes.
- [ ] Add RED cancellation-race tests for `cancelled_side_effects_possible` followed by inspection requirement.
- [ ] Add RED context-limit tests: estimate the complete `AgentContextAssembler` snapshot before every Worker step using provider-declared limits; never silently drop earlier results; true provider limit ends with `context_limit_reached`, stores a bounded continuation summary as private payload plus safe reference, and Continue cannot override it.
- [ ] Run RED across Core/Backend/Broker focused tests sequentially.
- [ ] Implement scanner, explicit transitions, context-size estimator, and deterministic bounded continuation-summary builder. Keep client disconnect non-terminal; headless run continues.
- [ ] Run GREEN. Expected: zero failures and no old execution replay.
- [ ] Commit: `feat(agent): reconcile ambiguous tool outcomes`.

## Task 9: Complete CLI terminal/reconcile UX and headless integration

**Files:**

- Modify `backend-dotnet/RuAgent.Cli/AgentCliParser.cs`.
- Modify `backend-dotnet/RuAgent.Cli/AgentCommands.cs`.
- Modify `backend-dotnet/RuAgent.Cli/SafeEventRenderer.cs`.
- Modify `backend-dotnet/RuAgent.Cli/NdjsonEventRenderer.cs`.
- Create `backend-dotnet/RuAgent.Cli.Tests/TerminalEventRendererTests.cs`.
- Create `backend-dotnet/RuAgent.Cli.Tests/ReconcileCommandTests.cs`.
- Create `backend-dotnet/RuBackend.Tests/Harness/HeadlessCodingLoopTests.cs`.
- Modify `backend-dotnet/RuAgent.Cli/README.md`.

- [ ] Add RED CLI tests for `reconcile --outcome applied|not-applied|unknown`, interactive approval previews, Continue, safe live terminal frames, dropped-frame notice, final head/tail payload fetched only on explicit user action, and `--json` never prompting.
- [ ] Add RED multi-client tests: Desktop-shaped test client and CLI attach concurrently, first valid approval wins, later gets `approval_stale`, both reconnect from independent cursors, one cancel is idempotent.
- [ ] Add a deterministic headless tracer with fake model steps: inspect files, edit, run failing test, repair, rerun green, show Git status, request managed commit approval, complete, and deliver one final answer. Seed unrelated dirty/staged variants.
- [ ] Run RED across CLI/Backend focused tests.
- [ ] Implement commands/renderers without CLI-side policy or loop logic.
- [ ] Run GREEN. Expected: zero failures.
- [ ] Commit: `feat(cli): complete headless coding workflow`.

## Task 10: Verify phase 3 and reconcile current docs

**Files:**

- Modify `README.md`, `backend-dotnet/README.md`, `docs/architecture.md`, `docs/operations/agent-tools.md`, `CONTEXT.md`, and `tests/test_current_documentation.py` for terminal/Git/reconciliation behavior now true on Agent v2 headless path. The current docs and documentation test must state positively that tests/builds/project code and Git hooks can execute arbitrary host code and open sockets, and that `network_intent` is audit/approval metadata rather than network isolation.
- Modify `backend-dotnet/RuAgent.Cli/README.md` with the complete milestone command list.
- Inspect `TODO.md`; retain OS sandbox as separate deferred work.
- Modify `build.bat` only for missing test/publish coverage.

- [ ] Search for false isolation/safety claims, stale terminal behavior, and placeholders:

```powershell
rg -n "terminal unavailable|approval every terminal|network isolation|filesystem sandbox|safe sandbox|TBD|TODO|FIXME|NotImplementedException" D:\LLM\README.md D:\LLM\backend-dotnet\README.md D:\LLM\CONTEXT.md D:\LLM\docs D:\LLM\backend-dotnet\RuAgent.Harness.Core D:\LLM\backend-dotnet\RuAgent.Harness.Persistence D:\LLM\backend-dotnet\RuAgent.Broker D:\LLM\backend-dotnet\RuAgent.Protocol D:\LLM\backend-dotnet\RuAgent.Cli D:\LLM\backend-dotnet\RuBackend\Harness
```

Expected: current docs state local-user authority, unknown-secret limitations, arbitrary-code/socket risk from allowed commands, and the audit-only nature of `network_intent`; only explicit deferred/historical TODOs remain.

- [ ] Run all relevant suites sequentially:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Cli.Tests\RuAgent.Cli.Tests.csproj -c Release --no-restore --nologo
& D:\LLM\.venv\Scripts\python.exe -m ruff check D:\LLM\src D:\LLM\tests D:\LLM\scripts D:\LLM\tools
& D:\LLM\.venv\Scripts\python.exe -m mypy D:\LLM\src
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_current_documentation.py
cmd /c D:\LLM\build.bat
```

Expected: all green, route matrix 54+2, build succeeds. Run sequentially.

- [ ] Run deterministic disposable-repo acceptance from CLI only: seeded bug, unrelated dirty work, edit/test/repair, cancel process tree, reconnect, Standard approvals, delete/restore, reconciliation, and separate approved local commit. Do not use the live Ru worktree.
- [ ] Inspect/delete only genuinely obsolete current docs; retain dated records.
- [ ] Inspect/stage phase-3 files only and commit: `feat(agent): complete headless coding loop`.
