# Ru Agent Session Rotation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make consecutive verified volume commands survive chat-session replacement and interpret Vietnamese target wording without weakening Broker ownership.

**Architecture:** Keep `RuAgentBroker` single-owner and move lease rotation into `AgentTurnCoordinator`, which will hold one active grant under its existing semaphore and close it before opening a different session. Keep volume selection deterministic in Python, but split absolute `lên N` wording from relative `thêm N`; all automated tests use fakes and never open the real Windows audio endpoint.

**Tech Stack:** Python 3.13, pytest, .NET 10, C#, xUnit, ASP.NET Core, private Agent WebSocket protocol, structured diagnostics.

---

## File map

- `src/ru_runtime/generation/volume_intent.py`: owns deterministic Vietnamese and English volume grammar only.
- `tests/test_volume_intent.py`: owns parser safety and operation semantics.
- `tests/test_worker_runtime_handlers.py`: proves the parsed target reaches one typed tool request without model generation.
- `backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs`: owns the Host's one active Broker lease and serialized close/open transition.
- `backend-dotnet/RuBackend.Tests/Conversation/AgentTurnCoordinatorTests.cs`: reproduces the real cross-session ownership failure with a stateful fake Broker.
- `backend-dotnet/RuAgent.Broker.Tests/BrokerSessionTests.cs`: preserves the Broker's existing single-owner defense.
- `docs/operations/agent-tools.md`: current operator-facing tool ownership and language contract.
- `docs/operations/runtime-behavior.md`: current runtime lease lifecycle.
- `docs/operations/verification.md`: deterministic versus native acceptance boundary.
- `tests/test_current_documentation.py`: prevents current docs from drifting back to the broken lifecycle.

No new production file is needed. The existing modules already have one clear owner for grammar, Host orchestration, and Broker policy.

### Task 1: Correct Vietnamese target-versus-delta semantics

**Files:**
- Modify: `tests/test_volume_intent.py:8-79`
- Modify: `tests/test_worker_runtime_handlers.py:626-721`
- Modify: `src/ru_runtime/generation/volume_intent.py:32-50`

- [ ] **Step 1: Add failing parser examples**

Add these cases to `test_explicit_commands_resolve_to_one_exact_tool_call`:

```python
        ("Tăng âm lượng lên 10", {"operation": "set", "level": 10}),
        ("Tăng âm lượng lên mức 10", {"operation": "set", "level": 10}),
        ("Tăng âm lượng thêm 10", {"operation": "increase", "amount": 10}),
```

Add the topic-free shorthand to
`test_negated_quoted_hypothetical_and_ordinary_prose_stays_non_action`:

```python
        "Tăng lên 10",
```

Add this range case to `test_unclear_or_invalid_volume_requests_never_execute`:

```python
        ("Tăng âm lượng lên 101", "volume_value_out_of_range"),
```

- [ ] **Step 2: Run the parser test and observe RED**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_volume_intent.py
```

Expected: the two `lên` target cases fail because the current parser returns
`{"operation": "increase", "amount": 10}`; the out-of-range target case also
uses the relative range.

- [ ] **Step 3: Add a failing Worker-dispatch regression**

Make `_verified_result` accept a target level and `_run_action_turn` accept an
explicit prompt:

```python
def _verified_result(level: int = 2) -> dict[str, object]:
    return {
        "status": "succeeded",
        "output": {"operation": "set", "level": level, "muted": False},
        "safe_summary": f"Đã xác minh âm lượng ở mức {level}%.",
        "error_code": None,
        "duration_ms": 12,
        "verification_state": "verified",
        "endpoint_role": "default_render_multimedia",
        "endpoint_hash": "0123456789abcdef",
    }


async def _run_action_turn(
    runner: SequencedRunner,
    result: dict[str, object],
    agent_mode: bool,
    *,
    prompt: str = "Hạ âm lượng còn 2%",
) -> list[WorkerReply]:
    tool_result_queue: asyncio.Queue = asyncio.Queue()
    handlers = build_runtime_handlers(RuntimeDependencies(runner=runner))
    replies = []
    stream = handlers.chat(
        _action_payload(tool_result_queue, agent_mode, prompt=prompt),
        asyncio.Event(),
    )
    replies.append(await anext(stream))
    request = await anext(stream)
    assert request.type == CHAT_TOOL_REQUEST
    replies.append(request)
    await tool_result_queue.put(
        {
            "request_id": request.payload["request_id"],
            "session_id": request.payload["session_id"],
            "correlation_id": request.payload["correlation_id"],
            **result,
        }
    )
    replies.extend([reply async for reply in stream])
    return replies
```

Add this test:

```python
@pytest.mark.parametrize("agent_mode", (False, True))
def test_vietnamese_target_increase_dispatches_absolute_set(
    agent_mode: bool,
) -> None:
    runner = SequencedRunner("model must not be called")

    replies = asyncio.run(_run_action_turn(
        runner,
        _verified_result(10),
        agent_mode,
        prompt="Tăng âm lượng lên 10",
    ))

    request = next(reply for reply in replies if reply.type == CHAT_TOOL_REQUEST)
    assert request.payload["arguments"] == {"operation": "set", "level": 10}
    assert runner.calls == []
    assert replies[-1].payload["raw_reply"] == "Em đã đặt âm lượng về 10% rồi anh."
```

- [ ] **Step 4: Run the Worker regression and observe RED**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_worker_runtime_handlers.py -k "vietnamese_target_increase"
```

Expected: FAIL because the tool request currently carries `increase/amount`.

- [ ] **Step 5: Implement the minimal grammar split**

Insert the target pattern before the existing absolute patterns and narrow the
Vietnamese relative-increase marker to `thêm`:

```python
_ABSOLUTE_PATTERNS = (
    re.compile(
        rf"(?:tăng).*?(?:âm lượng|tiếng máy|tiếng).*?"
        rf"(?:lên)\s*(?:mức\s*)?{_NUMBER}\s*%?"
    ),
    re.compile(
        rf"(?:đặt|chỉnh|hạ|giảm).*?(?:âm lượng|tiếng máy|tiếng).*?"
        rf"(?:xuống\s+)?(?:còn|về|tới|đến|ở mức)\s*{_NUMBER}\s*%?"
    ),
    re.compile(
        rf"(?:set|lower|turn down).*?(?:volume|sound).*?"
        rf"(?:to|at)\s*{_NUMBER}\s*(?:%|percent)?"
    ),
)
_RELATIVE_PATTERNS = (
    (
        "increase",
        re.compile(
            rf"(?:tăng).*?(?:âm lượng|tiếng máy|tiếng).*?"
            rf"(?:thêm)\s*{_NUMBER}\s*%?"
        ),
    ),
```

Keep the remaining decrease and English relative patterns unchanged.

- [ ] **Step 6: Run focused Python tests and observe GREEN**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_volume_intent.py D:\LLM\tests\test_worker_runtime_handlers.py
```

Expected: PASS with no model call in either normal chat or Agent mode.

- [ ] **Step 7: Commit the grammar slice**

```powershell
git add -- src/ru_runtime/generation/volume_intent.py tests/test_volume_intent.py tests/test_worker_runtime_handlers.py
git commit -m "fix: distinguish volume targets from deltas"
```

### Task 2: Rotate the Host's single active Broker lease

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker.Tests/BrokerSessionTests.cs:8-43`
- Modify: `backend-dotnet/RuBackend.Tests/Conversation/AgentTurnCoordinatorTests.cs:11-371`
- Modify: `backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs:34-265`

- [ ] **Step 1: Characterize the Broker single-owner invariant**

Add this passing defense test to `BrokerSessionTests`:

```csharp
[Fact]
public void A_different_owner_must_wait_until_the_active_session_closes()
{
    using var fixture = DescriptorFixture.Create();
    var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
    var scopes = new HashSet<string>(["system.write"], StringComparer.Ordinal);
    var skills = new HashSet<string>(StringComparer.Ordinal);

    Assert.True(session.Open("session-1", scopes, skills));
    Assert.False(session.Open("session-2", scopes, skills));

    session.Close("session-1");

    Assert.True(session.Open("session-2", scopes, skills));
}
```

Run:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~BrokerSessionTests"
```

Expected: PASS, proving the Broker must not be changed to accept concurrent
owners.

- [ ] **Step 2: Make the coordinator fake enforce that invariant**

Extend `Request` with stable per-turn IDs:

```csharp
private static AgentTurnRequest Request(
    string toolId = "system.volume",
    string toolVersion = "1.0.0",
    string capabilityClass = "native_mutation",
    string requestId = "tool-request-1",
    string sessionId = "session-1",
    string correlationId = "correlation-1") => new(
    new WorkerToolRequest(
        requestId,
        sessionId,
        correlationId,
        toolId,
        toolVersion,
        capabilityClass,
        new JsonObject
        {
            ["operation"] = "set",
            ["level"] = 2,
        },
        30_000));
```

Give `FakeBroker` one active owner and a call trace:

```csharp
public string? ResultRequestId { get; init; }
public string? CloseErrorCode { get; set; }
public string? ActiveSessionId { get; private set; }
public int CloseCount { get; private set; }
public List<string> Calls { get; } = [];

public Task<AgentSessionGrant> OpenSessionAsync(
    string sessionId,
    IReadOnlySet<string> scopes,
    IReadOnlySet<string> skillIds,
    string? correlationId,
    int deadlineMs,
    CancellationToken cancellationToken)
{
    Calls.Add($"open:{sessionId}");
    if (ActiveSessionId is not null
        && !string.Equals(ActiveSessionId, sessionId, StringComparison.Ordinal))
    {
        throw new AgentBrokerException("session_conflict");
    }

    ActiveSessionId = sessionId;
    OpenCount++;
    LastRequestedScopes = scopes;
    LastRequestedSkillIds = skillIds;
    return Task.FromResult(new AgentSessionGrant(
        sessionId,
        ActualScopes,
        new HashSet<string>(StringComparer.Ordinal),
        DateTimeOffset.UtcNow.AddMinutes(1)));
}
```

Replace the fake `ExecuteToolAsync` with this complete stateful method:

```csharp
public async Task<AgentToolResult> ExecuteToolAsync(
    AgentToolCall call,
    CancellationToken cancellationToken)
{
    Calls.Add($"execute:{call.SessionId}");
    if (!string.Equals(ActiveSessionId, call.SessionId, StringComparison.Ordinal))
    {
        throw new AgentBrokerException("broker_not_ready");
    }

    ExecuteCount++;
    LastCall = call;
    ExecutionStarted.TrySetResult();
    if (BlockExecution)
    {
        await ReleaseExecution.Task.ConfigureAwait(false);
    }

    return new AgentToolResult(
        ResultRequestId ?? call.RequestId,
        ResultStatus,
        new JsonObject { ["operation"] = "set", ["level"] = 2 },
        ResultErrorCode,
        ResultSafeSummary,
        12,
        ResultVerificationState,
        ResultEndpointRole,
        ResultEndpointHash,
        AdapterInvoked);
}
```

Replace the fake close method with:

```csharp
public Task CloseSessionAsync(
    string sessionId,
    string? correlationId,
    CancellationToken cancellationToken)
{
    Calls.Add($"close:{sessionId}");
    CloseCount++;
    if (CloseErrorCode is not null)
    {
        throw new AgentBrokerException(CloseErrorCode);
    }

    if (string.Equals(ActiveSessionId, sessionId, StringComparison.Ordinal))
    {
        ActiveSessionId = null;
    }

    return Task.CompletedTask;
}
```

- [ ] **Step 3: Add the failing cross-session tests**

Add these tests to `AgentTurnCoordinatorTests`:

```csharp
[Fact]
public async Task A_new_chat_session_closes_the_old_lease_before_opening()
{
    var broker = new FakeBroker();
    var coordinator = new AgentTurnCoordinator(broker);

    var first = await coordinator.ExecuteToolAsync(
        Request(requestId: "tool-1", sessionId: "session-1", correlationId: "correlation-1"),
        CancellationToken.None);
    var second = await coordinator.ExecuteToolAsync(
        Request(requestId: "tool-2", sessionId: "session-2", correlationId: "correlation-2"),
        CancellationToken.None);

    Assert.Equal(ToolResultStatuses.Succeeded, first.Status);
    Assert.Equal(ToolResultStatuses.Succeeded, second.Status);
    Assert.Equal(
        ["open:session-1", "execute:session-1", "close:session-1", "open:session-2", "execute:session-2"],
        broker.Calls);
}

[Fact]
public async Task Repeated_tools_in_one_session_reuse_the_valid_lease()
{
    var broker = new FakeBroker();
    var coordinator = new AgentTurnCoordinator(broker);

    _ = await coordinator.ExecuteToolAsync(
        Request(requestId: "tool-1", correlationId: "correlation-1"),
        CancellationToken.None);
    _ = await coordinator.ExecuteToolAsync(
        Request(requestId: "tool-2", correlationId: "correlation-2"),
        CancellationToken.None);

    Assert.Equal(1, broker.OpenCount);
    Assert.Equal(0, broker.CloseCount);
    Assert.Equal(["open:session-1", "execute:session-1", "execute:session-1"], broker.Calls);
}

[Fact]
public async Task Failed_close_blocks_the_replacement_session_and_adapter()
{
    var broker = new FakeBroker();
    var coordinator = new AgentTurnCoordinator(broker);
    _ = await coordinator.ExecuteToolAsync(Request(), CancellationToken.None);
    broker.CloseErrorCode = "broker_unavailable";

    var result = await coordinator.ExecuteToolAsync(
        Request(requestId: "tool-2", sessionId: "session-2", correlationId: "correlation-2"),
        CancellationToken.None);

    Assert.Equal(ToolResultStatuses.Failed, result.Status);
    Assert.Equal("broker_unavailable", result.ErrorCode);
    Assert.Equal("session-1", broker.ActiveSessionId);
    Assert.Equal(1, broker.OpenCount);
    Assert.Equal(1, broker.ExecuteCount);
}

[Fact]
public async Task Incomplete_new_grant_is_closed_without_adapter_execution()
{
    var broker = new FakeBroker
    {
        ActualScopes = new HashSet<string>(StringComparer.Ordinal),
    };

    var result = await new AgentTurnCoordinator(broker).ExecuteToolAsync(
        Request(),
        CancellationToken.None);

    Assert.Equal(ToolResultStatuses.Failed, result.Status);
    Assert.Equal("scope_not_granted", result.ErrorCode);
    Assert.Null(broker.ActiveSessionId);
    Assert.Equal(0, broker.ExecuteCount);
    Assert.Equal(["open:session-1", "close:session-1"], broker.Calls);
}
```

- [ ] **Step 4: Run the coordinator tests and observe RED**

Run:

```powershell
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentTurnCoordinatorTests"
```

Expected: the cross-session test returns `session_conflict`, the failed-close
test never calls close, and the incomplete-grant test leaves the fake session
open.

- [ ] **Step 5: Replace the Host dictionary with one active lease**

Replace the field:

```csharp
private AgentSessionGrant? _activeSession;
```

Replace `EnsureSessionAsync` with:

```csharp
private async Task<(AgentSessionGrant Grant, bool Opened)> EnsureSessionAsync(
    WorkerToolRequest tool,
    AgentToolCapability capability,
    string correlationId,
    CancellationToken cancellationToken)
{
    await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        var existing = _activeSession;
        if (existing is not null
            && string.Equals(existing.SessionId, tool.SessionId, StringComparison.Ordinal)
            && existing.ExpiresAt > DateTimeOffset.UtcNow
            && capability.Scopes.All(existing.Scopes.Contains))
        {
            return (existing, false);
        }

        if (existing is not null)
        {
            await _broker!.CloseSessionAsync(
                existing.SessionId,
                correlationId,
                cancellationToken).ConfigureAwait(false);
            _activeSession = null;
        }

        var grant = await _broker!.OpenSessionAsync(
            tool.SessionId,
            capability.Scopes.ToHashSet(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            correlationId,
            tool.DeadlineMs,
            cancellationToken).ConfigureAwait(false);
        if (!capability.Scopes.All(grant.Scopes.Contains))
        {
            // Track ownership until the defensive close succeeds so a failed
            // cleanup can be retried without forgetting the Broker owner.
            _activeSession = grant;
            await _broker.CloseSessionAsync(
                grant.SessionId,
                correlationId,
                cancellationToken).ConfigureAwait(false);
            _activeSession = null;
            throw new AgentBrokerException("scope_not_granted");
        }

        _activeSession = grant;
        return (grant, true);
    }
    finally
    {
        _sessionLock.Release();
    }
}
```

Keep the existing post-`EnsureSessionAsync` scope check as defense in depth.

- [ ] **Step 6: Run focused .NET tests and observe GREEN**

Run sequentially:

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~BrokerSessionTests"
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentTurnCoordinatorTests"
```

Expected: PASS; the trace proves close occurs before the replacement open and
no adapter executes after a failed close or incomplete grant.

- [ ] **Step 7: Commit the ownership fix**

```powershell
git add -- backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs backend-dotnet/RuBackend.Tests/Conversation/AgentTurnCoordinatorTests.cs backend-dotnet/RuAgent.Broker.Tests/BrokerSessionTests.cs
git commit -m "fix: rotate agent session leases safely"
```

### Task 3: Update the current operating contract

**Files:**
- Modify: `tests/test_current_documentation.py:139-173`
- Modify: `docs/operations/agent-tools.md:31-88`
- Modify: `docs/operations/runtime-behavior.md:134-165`
- Modify: `docs/operations/verification.md:60-127`

- [ ] **Step 1: Add failing current-documentation assertions**

Extend `test_current_docs_describe_deterministic_volume_recovery_truthfully`:

```python
    assert "one active Broker lease" in agent_tools_flat
    assert "closes the previous lease before opening" in agent_tools_flat
    assert "tăng âm lượng lên N" in agent_tools
    assert "tăng âm lượng thêm N" in agent_tools
    assert "cross-session rotation" in verification_flat
```

- [ ] **Step 2: Run the documentation contract and observe RED**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_current_documentation.py
```

Expected: FAIL because current operations docs do not describe lease rotation
or target-versus-delta wording.

- [ ] **Step 3: Update the current operations docs**

Add this paragraph after the execution-chain diagram in `agent-tools.md`:

```markdown
The Public Host keeps one active Broker lease. It reuses a valid matching
session, and closes the previous lease before opening a different chat session.
Close or open failure stops before adapter execution; the Broker continues to
reject concurrent owners with `session_conflict`.
```

Add this paragraph under `Volume and adapter boundaries`:

```markdown
For deterministic Vietnamese commands, `tăng âm lượng lên N` is an absolute
`set` target, while `tăng âm lượng thêm N` is a relative `increase`. Topic-free
shorthand is not promoted into an action from conversation history.
```

Add this lifecycle sentence to `runtime-behavior.md`:

```markdown
The Host serializes one active Broker grant: a matching valid session is reused;
a replacement chat session closes the old grant before open. Failure is
fail-closed and never reaches the native adapter.
```

Add this evidence sentence to `verification.md`:

```markdown
Host tests cover cross-session rotation with a stateful single-owner fake and
prove close-before-open, same-session reuse, and no dispatch after close failure.
```

Do not rewrite superseded historical plans.

- [ ] **Step 4: Run docs tests and audit obsolete claims**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_current_documentation.py
rg -n "multiple active Broker|concurrent Agent Sessions|tăng âm lượng lên.*increase|awaiting written-spec review" D:\LLM\README.md D:\LLM\docs\operations D:\LLM\docs\superpowers\specs D:\LLM\tests
```

Expected: documentation tests PASS; the scan returns no current contradictory
claim. Historical examples are reviewed manually and left untouched when
clearly marked superseded.

- [ ] **Step 5: Commit current documentation**

```powershell
git add -- docs/operations/agent-tools.md docs/operations/runtime-behavior.md docs/operations/verification.md tests/test_current_documentation.py
git commit -m "docs: document agent lease rotation"
```

### Task 4: Verify the complete repair without native side effects

**Files:**
- Verify only; no production file should be added in this task.
- Generated artifact: `artifacts/aspnetcore/ru-agent-session-rotation.json`

- [ ] **Step 1: Run focused regression gates**

Run sequentially:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q D:\LLM\tests\test_volume_intent.py D:\LLM\tests\test_worker_runtime_handlers.py D:\LLM\tests\test_current_documentation.py
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~BrokerSessionTests"
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentTurnCoordinatorTests"
```

Expected: all focused tests PASS.

- [ ] **Step 2: Run full deterministic suites sequentially**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo
```

Expected: every suite PASS. Run them one at a time to avoid Windows pagefile
pressure.

- [ ] **Step 3: Run route, build, and backend Harness gates**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output D:\LLM\artifacts\aspnetcore\ru-agent-session-rotation.json
cmd /c D:\LLM\build.bat
```

Expected: route matrix remains `41 HTTP routes, 2 WebSocket routes`; Harness
returns success and cleans owned processes; build exits 0.

- [ ] **Step 4: Check repository and diagnostic hygiene**

Run:

```powershell
git diff --check
rg -n "\[DEBUG-[^]]+\]" D:\LLM\src D:\LLM\tests D:\LLM\backend-dotnet D:\LLM\gui-winui3
git status --short --branch
```

Expected: no whitespace errors, no temporary debug markers, and only the
intended plan/artifact state remains. Do not commit generated Harness artifacts
unless they are already tracked release evidence.

- [ ] **Step 5: Push and hand off live acceptance**

After all deterministic gates pass:

```powershell
git push origin main
git rev-parse HEAD
git rev-parse origin/main
```

Expected: both revisions match. Ask the user to retry in the GUI:

1. Set volume to 2 in one chat.
2. Start or switch to another chat.
3. Say `Tăng âm lượng lên 10`.
4. Confirm Windows ends at exactly 10, not 12, and Ru reports success only after
   the actual level changes.

This user-run check is the only acceptance claim about the real audio endpoint.
