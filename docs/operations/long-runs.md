# Long runs and their ceilings

A root run works until the model finishes, the user stops it, or a failure ends it. No step,
tool-call or wall-clock budget ends it: when a budget window of 128 model steps or 256 tool
calls fills, the run opens the next window by itself. This page lists every remaining bound a
long run can meet, what happens there, and the one place to change it. Each value is a named
constant, so changing a ceiling is a one-line edit plus the tests that pin it.

Verified by the asserting soak (`HeadlessCodingLoopTests.Soak`, see [below](#soak)): 1,700
read steps on a 1M-token model, 600 steps with 50 KB of replayed reasoning each, and 600 mixed
steps that read, write files and launch PowerShell, each completing with compactions and no
request reaching the history window.

## What ends or pauses a run

| Bound | Value | At the bound | Where to change it |
|---|---|---|---|
| Provider retries for one step | 10 attempts, 2 s to 5 min backoff (about 12 min) | The run parks for Continue with the provider code | `ProviderRetryPolicy`, `InProcessModelStepRunner.ModelStepRetryBackoffs` |
| Server `Retry-After` honored automatically | 5 min | Longer hints park the step at once | `ProviderRetryPolicy.MaximumAutomaticDelay` |
| Pending approval | 24 h | The run fails as `approval_expired` | `ApprovalLimits.PendingApprovalLifetimeMinutes` (Protocol), shared by the Host and the Broker |
| Remembered session approval | 24 h, 64 per session | The same intent asks again | `SessionApprovalLedger.EntryLifetime` (follows `ApprovalLimits`) |
| Workspace trust challenge | 10 min | The pending run is cancelled; trust again | `HarnessRecoveryScanner.WorkspaceTrustTimeout` |
| Model context | the pinned model's window | Automatic compaction at 80%; `context_limit_reached` only if compaction cannot shrink it | `ContextBudgetPolicyOptions` |

For an unattended long run, use Standard (workspace edits and terminal run without asking)
or Full Access (no approval prompts at all; explicit deny rules, timeouts and process-tree
bounds stay), so the run rarely waits on the 24-hour approval lifetime.

## What a run keeps (never ends it)

| Bound | Value | Behavior | Where to change it |
|---|---|---|---|
| Broker run lease and session | no clock expiry | Closed when the run ends, its session closes or the Host stops | `BrokerGatewayContracts.LeaseExpiresAt` |
| History window sent per request | 4,096 messages / 32 MiB | Compacts at 75% fill, so the oldest history reaches a checkpoint before it would be dropped | `ModelInputLimits`, `AgentRunCompaction.HistoryWindowCompactionFill` |
| Replayed reasoning (provider continuations) | the history window's rows and bytes | Follows the window | `SqliteProviderContinuationReader` |
| One request's memory | 64 MiB | Request is refused by name | `ModelInputLimits.MaxRequestBytes` |
| Activity replay when a run is reopened | newest 500 rows | Older rows stay in the durable log | `AgentActivitiesService.MaxReplayedActivities` |
| GUI live feed reconnects | 3 in a row without new events | Any delivered event refills the budget | `AgentRunClientAdapter` (`ResetResumeBudgetOnProgress`) |
| Host idle shutdown | 10 min without requests | Every registered run, parked ones included, keeps the Host up | `RuAgent.Hosting` idle policy |

## Per-call bounds (each call, not the run)

| Bound | Value | Where to change it |
|---|---|---|
| Foreground terminal call | 30 min hard maximum; over 15 min asks in Standard | `TerminalLimits.HardMaxTimeoutMs`, `StandardApprovalThresholdMs` |
| Background terminal task | 30 min default, 24 h maximum, 8 concurrent per session | `TerminalLimits.BackgroundDefaultLifetimeMs`, `BackgroundMaxLifetimeMs`, `MaxBackgroundTasksPerSession` |
| Retained terminal output | 256 Ki characters in memory, older spools evicted past 32 per session | `TerminalLimits.MaxSpoolChars`, `TerminalOutputSpool.MaxSpoolsPerSession` |
| One structured file mutation | 64 files / 16 MiB per atomic call | `FileMutationJournal` |
| MCP tool call | 10 min | `McpConfiguration.MaxCallTimeout` |
| Subagents | 3 runnable, 3 attempts, 10 min per wait (the root parks, it does not fail) | `SubagentLimits` |

`TerminalLimits` values appear in tool schemas, so changing one also updates
`AgentToolGoldenLockTests` (schema JSON and digest).

## Soak

The soak runs the real Host (SQLite stores, coordinator, compaction, in-process runner,
continuation replay) against a fake DeepSeek-style provider. It is off unless
`RU_SOAK_STEPS` is set:

```powershell
$env:RU_SOAK_STEPS = '600'; $env:RU_SOAK_ASSERT = '1'
$env:RU_SOAK_MIX = '1'            # rotate read, write_file and powershell
$env:RU_SOAK_REASONING_KB = '50'  # replayed reasoning per step
$env:RU_SOAK_MINUTES = '60'       # wait for the run
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --filter "FullyQualifiedName~Soak_long_run"
```

With `RU_SOAK_ASSERT=1` the run must complete, no request may reach the history window, a
reopened run must replay its newest activity, and in mixed mode every write must land and
every tool call (terminal launches included) must succeed without an approval.
