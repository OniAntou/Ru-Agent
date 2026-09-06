# Agent Activity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship per-run grouped Agent Activity timeline with safe tool labels and durable traceability.

**Architecture:** Extend the existing Core projector and transient SSE contract with one optional safe label, keep Core as the only authority, retain per-run UI timeline instead of clearing on completion.

**Tech Stack:** .NET 10, C# 12, ASP.NET Core, WinUI 3, xUnit/dotnet test

**Spec:** docs/superpowers/specs/2026-09-06-agent-activity-design.md

## Global Constraints

- Privacy boundary holds: no command, arguments, argv, path, diff, output, stdout, stderr, body, environment, exception text in activity.
- FileName stays basename only, max 128 chars, no slashes, no drive colon, no reserved device names.
- SafeCode stays lowercase alnum plus underscore and dot, max 64 chars.
- ToolDisplay is fixed-table derived from ToolId only, ASCII max 32 chars, optional, never from user input.
- Activity publish failures never fail a run; malformed transient frames are ignored.
- No new public HTTP route and no new SQLite table in this plan.

---

## File Structure

- Modify: `backend-dotnet/RuAgent.Protocol/Harness/AgentActivityContracts.cs` — adds optional `tool_display` to `AgentActivityWire`, allowlist, validator.
- Modify: `backend-dotnet/RuAgent.Harness.Core/AgentActivity.cs` — adds optional `ToolDisplay` to Core record.
- Modify: `backend-dotnet/RuAgent.Harness.Core/AgentActivityProjector.cs` — fixed ToolId to label map, passes ToolDisplay through all projectors.
- Modify: `backend-dotnet/RuAgent.Client/AgentActivityModel.cs` — parses and exposes `ToolDisplay`.
- Modify: `backend-dotnet/RuBackend/Harness/AgentActivityLivePublisher.cs` — passes ToolDisplay Core to Protocol.
- Modify: `backend-dotnet/RuBackend/Harness/AgentConversationActivityProjector.cs` — adds assistant completion projection with runId link.
- Modify: `gui-winui3/Models/AgentActivityItem.cs` — exposes ToolDisplay, uses DurationMs for elapsed text.
- Modify: `gui-winui3/Views/ChatView.xaml.cs` — per-run retention, in-place update, no early Thinking delete.
- Test: `backend-dotnet/RuBackend.Tests/Harness/AgentActivityProtocolTests.cs` — extend.
- Test: `backend-dotnet/RuAgent.Harness.Core.Tests/AgentActivityProjectorTests.cs` — extend.
- Test: `backend-dotnet/RuAgent.Client.Tests/AgentActivityModelTests.cs` — extend.
- Test: `gui-winui3.Tests/AgentActivityItemTests.cs` — extend.

---

### Task 1: Protocol tool_display contract

**Files:**
- Modify: `backend-dotnet/RuAgent.Protocol/Harness/AgentActivityContracts.cs`
- Test: `backend-dotnet/RuBackend.Tests/Harness/AgentActivityProtocolTests.cs`

**Interfaces:**
- Consumes: existing `AgentActivityWire`, `AgentActivityWireValidator`, `AgentActivityWireCodec`.
- Produces: `AgentActivityWire(..., string? ToolDisplay = null)` with `tool_display` JSON name, `MaximumToolDisplayLength = 32`, validator `ValidateToolDisplay`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Serialize_Includes_ToolDisplay_When_Set()
{
    var activity = new AgentActivityWire("act_abc123", AgentActivityKind.Analyze, AgentActivityState.Completed, ToolDisplay: "View");
    var json = AgentActivityWireCodec.Serialize(activity);
    Assert.Contains("tool_display", json, StringComparison.Ordinal);
    var parsed = AgentActivityWireCodec.Parse(json);
    Assert.Equal("View", parsed.ToolDisplay);
}

[Fact]
public void Parse_Old_Payload_Without_ToolDisplay_Succeeds()
{
    var parsed = AgentActivityWireCodec.Parse("{\"activity_id\":\"act_abc123\",\"kind\":\"analyze\",\"state\":\"completed\",\"safe_code\":\"succeeded\"}");
    Assert.Null(parsed.ToolDisplay);
}

[Fact]
public void Parse_Rejects_ToolDisplay_With_Slash()
{
    Assert.Throws<JsonException>(() => AgentActivityWireCodec.Parse("{\"activity_id\":\"act_abc123\",\"kind\":\"analyze\",\"state\":\"completed\",\"tool_display\":\"a/b\",\"safe_code\":\"succeeded\"}"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~AgentActivityProtocolTests.Serialize_Includes_ToolDisplay"`
Expected: FAIL with compile error because ToolDisplay does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
public sealed record AgentActivityWire(
    [property: JsonPropertyName("activity_id")] string ActivityId,
    [property: JsonPropertyName("kind")] AgentActivityKind Kind,
    [property: JsonPropertyName("state")] AgentActivityState State,
    [property: JsonPropertyName("file_name")] string? FileName = null,
    [property: JsonPropertyName("line_start")] int? LineStart = null,
    [property: JsonPropertyName("line_end")] int? LineEnd = null,
    [property: JsonPropertyName("symbol")] string? Symbol = null,
    [property: JsonPropertyName("count")] int? Count = null,
    [property: JsonPropertyName("added")] int? Added = null,
    [property: JsonPropertyName("removed")] int? Removed = null,
    [property: JsonPropertyName("duration_ms")] int? DurationMs = null,
    [property: JsonPropertyName("safe_code")] string SafeCode = "succeeded",
    [property: JsonPropertyName("tool_display")] string? ToolDisplay = null);
```

Add to validator:

```csharp
public const int MaximumToolDisplayLength = 32;
AllowedProperties.Add("tool_display");
ValidateToolDisplay(activity.ToolDisplay);
```

```csharp
private static void ValidateToolDisplay(string? toolDisplay)
{
    if (toolDisplay is null) return;
    if (toolDisplay.Length is < 1 or > MaximumToolDisplayLength) throw new JsonException("Tool display is invalid.");
    foreach (var c in toolDisplay)
    {
        var ok = (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') || (c is >= '0' and <= '9') || c is ' ' or '_' or '-' or '.';
        if (!ok) throw new JsonException("Tool display is invalid.");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~AgentActivityProtocolTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Protocol/Harness/AgentActivityContracts.cs backend-dotnet/RuBackend.Tests/Harness/AgentActivityProtocolTests.cs
git commit -m "feat(agent-activity): add tool_display to activity wire contract"
```

---

### Task 2: Core projector fixed label map

**Files:**
- Modify: `backend-dotnet/RuAgent.Harness.Core/AgentActivity.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Core/AgentActivityProjector.cs`
- Test: `backend-dotnet/RuAgent.Harness.Core.Tests/AgentActivityProjectorTests.cs`

**Interfaces:**
- Consumes: `ToolProposal(ToolId, ToolCallId, Arguments)`, `ToolExecutionReceipt`, Task 1 `ToolDisplay` field.
- Produces: `AgentActivityProjector.ProjectTool` returns `AgentActivity` with `ToolDisplay` set from fixed table; `ProjectModelStep` and `ProjectStatus` return `ToolDisplay: null`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void ProjectTool_Maps_FilesRead_To_View_Label()
{
    var proposal = Proposal("files.read", "{\"path\":\"D:\\\\LLM\\\\a.txt\"}");
    var activity = AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Started);
    Assert.Equal(AgentActivityKind.Analyze, activity.Kind);
    Assert.Equal("View", activity.ToolDisplay);
}

[Fact]
public void ProjectTool_Maps_Unknown_Tool_To_Null_Label()
{
    var proposal = Proposal("web.search", "{\"query\":\"x\"}");
    var activity = AgentActivityProjector.ProjectTool(proposal, AgentActivityState.Started);
    Assert.Null(activity.ToolDisplay);
}

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --nologo --filter "FullyQualifiedName~AgentActivityProjectorTests.ProjectTool_Maps_FilesRead"`
Expected: FAIL because ToolDisplay is null or missing.

- [ ] **Step 3: Write minimal implementation**

```csharp
public sealed record AgentActivity(
    string ActivityId,
    AgentActivityKind Kind,
    AgentActivityState State,
    string? FileName = null,
    int? LineStart = null,
    int? LineEnd = null,
    string? Symbol = null,
    int? Count = null,
    int? Added = null,
    int? Removed = null,
    int? DurationMs = null,
    string SafeCode = "succeeded",
    string? ToolDisplay = null);
```

In `ProjectTool`, after kind switch:

```csharp
var toolDisplay = proposal.ToolId switch
{
    "files.list" or "files.glob" => "Explore",
    "files.search" => "Search",
    "files.read" => "View",
    "files.write" or "files.edit" => "Edit",
    "terminal.run" or "terminal.shell" => "Terminal",
    "help.docs" => "Docs",
    _ => null,
};
```

Pass `ToolDisplay: toolDisplay` into the returned `AgentActivity`. `ProjectModelStep` passes `ToolDisplay: null`. `ProjectStatus` passes `ToolDisplay: null`. `ProjectDropped` passes `ToolDisplay: null`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --nologo --filter "FullyQualifiedName~AgentActivityProjectorTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Harness.Core/AgentActivity.cs backend-dotnet/RuAgent.Harness.Core/AgentActivityProjector.cs backend-dotnet/RuAgent.Harness.Core.Tests/AgentActivityProjectorTests.cs
git commit -m "feat(agent-activity): map fixed tool display labels in projector"
```

---

### Task 3: Client model plus live publisher passthrough

**Files:**
- Modify: `backend-dotnet/RuAgent.Client/AgentActivityModel.cs`
- Modify: `backend-dotnet/RuBackend/Harness/AgentActivityLivePublisher.cs`
- Test: `backend-dotnet/RuAgent.Client.Tests/AgentActivityModelTests.cs`
- Test: `backend-dotnet/RuBackend.Tests/Harness/AgentActivityLivePublisherTests.cs`

**Interfaces:**
- Consumes: Task 1 wire `ToolDisplay`, Task 2 Core `ToolDisplay`.
- Produces: `AgentActivityModel` with `ToolDisplay` property parsed from wire; `AgentActivityLivePublisher.Publish` forwards Core ToolDisplay to wire.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void TryParse_Preserves_ToolDisplay()
{
    var frame = Frame("agent.activity", new { activity_id = "act_11111111111111111111111111111111", kind = "analyze", state = "completed", tool_display = "View", safe_code = "succeeded" }, transientSequence: 7);
    Assert.True(AgentActivityModel.TryParse(frame, out var model));
    Assert.Equal("View", model!.ToolDisplay);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Client.Tests/RuAgent.Client.Tests.csproj --nologo --filter "FullyQualifiedName~AgentActivityModelTests.TryParse_Preserves_ToolDisplay"`
Expected: FAIL because ToolDisplay property does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
public sealed record AgentActivityModel(
    string ActivityId,
    AgentActivityKind Kind,
    AgentActivityState State,
    string? FileName,
    int? LineStart,
    int? LineEnd,
    string? Symbol,
    int? Count,
    int? Added,
    int? Removed,
    int? DurationMs,
    string SafeCode,
    string? ToolDisplay,
    long? DurableSequence,
    long? TransientSequence)
```

In `TryParse`, pass `wire.ToolDisplay` before frame sequences. In `AgentActivityLivePublisher.Publish`, pass `activity.ToolDisplay` into `ProtocolActivityWire`. Update the existing `FakeClient` activity construction in `gui-winui3.Tests/AgentRunControllerTests.cs:155-169` to insert `ToolDisplay: null` before the final two cursor args so the 14-arg call becomes the new 15-arg shape.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Client.Tests/RuAgent.Client.Tests.csproj --nologo --filter "FullyQualifiedName~AgentActivityModelTests"`
Expected: PASS

Run: `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~AgentActivityLivePublisherTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Client/AgentActivityModel.cs backend-dotnet/RuBackend/Harness/AgentActivityLivePublisher.cs backend-dotnet/RuAgent.Client.Tests/AgentActivityModelTests.cs
git commit -m "feat(agent-activity): forward tool display to transient clients"
```

---

### Task 4: Durable assistant completion projection

**Files:**
- Modify: `backend-dotnet/RuBackend/Harness/AgentConversationActivityProjector.cs`
- Test: `backend-dotnet/RuBackend.Tests/Harness/AgentReplyPolicyTests.cs`

**Interfaces:**
- Consumes: `IActivityLogStore.AppendAsync`, existing `ProjectUserAsync`, existing `FakeActivityLogStore` in `AgentReplyPolicyTests.cs:328-342`.
- Produces: `ProjectAssistantAsync(intentId, runId, sessionId, content, CancellationToken)` writing type `agent` role assistant with sanitized preview.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Assistant_activity_projector_is_idempotent_and_uses_safe_agent_shape()
{
    var activity = new FakeActivityLogStore();
    var projector = new AgentConversationActivityProjector(activity);
    var intent = new EventId("evt_22222222222222222222222222222222");
    var run = Run();
    await projector.ProjectAssistantAsync(intent, run.RunId, run.SessionId.Value, "Done", CancellationToken.None);
    await projector.ProjectAssistantAsync(intent, run.RunId, run.SessionId.Value, "different replay", CancellationToken.None);
    var evt = Assert.Single(activity.Events);
    Assert.Equal("agent", evt.Type);
    Assert.Equal("assistant", evt.Data["role"]!.GetValue<string>());
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~AgentReplyPolicyTests.Assistant_activity_projector_is_idempotent"`
Expected: FAIL because ProjectAssistantAsync does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
public async Task ProjectAssistantAsync(
    HarnessEventId intentId,
    RunId runId,
    string sessionId,
    string content,
    CancellationToken cancellationToken)
{
    _ = intentId.Value;
    _ = runId.Value;
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    ArgumentNullException.ThrowIfNull(content);
    var key = intentId.Value + ":assistant";
    if (!_projected.TryAdd(key, 0)) return;
    try
    {
        await _activity.AppendAsync(new ActivityEvent(
            $"agent:{runId.Value}:{intentId.Value}:assistant",
            0, string.Empty, "agent", sessionId,
            new JsonObject { ["role"] = "assistant", ["content"] = content, ["run_id"] = runId.Value }),
            cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        _projected.TryRemove(key, out _);
        throw;
    }
    catch
    {
        _logger?.LogWarning("agent_assistant_activity_projection_failed");
    }
}
```

The `run_id` extra field is dropped by `ActivityPreview.SanitizeEvent` for type agent, which keeps only role, preview, original_length, redacted. Traceability comes from the event_id prefix `agent:{runId}:{intentId}:assistant`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --nologo --filter "FullyQualifiedName~AgentReplyPolicyTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuBackend/Harness/AgentConversationActivityProjector.cs backend-dotnet/RuBackend.Tests/Harness/AgentReplyPolicyTests.cs
git commit -m "feat(agent-activity): project durable assistant completion"
```

---

### Task 5: WinUI per-run timeline retention

**Files:**
- Modify: `gui-winui3/Models/AgentActivityItem.cs`
- Modify: `gui-winui3/Views/ChatView.xaml.cs`
- Test: `gui-winui3.Tests/AgentActivityItemTests.cs`

**Interfaces:**
- Consumes: Task 3 `AgentActivityModel.ToolDisplay`.
- Produces: `AgentActivityItem.ToolDisplay` shown in metadata; `ChatView` keeps items per run until next run starts, updates in place by id, shows elapsed duration for Started.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void DisplayText_Prefers_ToolDisplay_For_Started_Terminal()
{
    var item = new AgentActivityItem(Activity(AgentActivityState.Started, kind: AgentActivityKind.Terminal, toolDisplay: "Terminal", durationMs: 2500));
    Assert.Contains("Terminal", item.DisplayText, StringComparison.Ordinal);
}
```

Extend the private `Activity` helper in `AgentActivityItemTests.cs:116-142` with `string? toolDisplay = null` and pass it as the new 13th positional arg before the existing `null, 1` cursors.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --nologo --filter "FullyQualifiedName~AgentActivityItemTests.DisplayText_Prefers_ToolDisplay"`
Expected: FAIL because ToolDisplay does not exist on the model or item.

- [ ] **Step 3: Write minimal implementation**

In `AgentActivityItem.cs`:

```csharp
public string? ToolDisplay { get; private set; }
```

Set it in `Apply` from `activity.ToolDisplay`. Prepend it in `MetadataText` when present:

```csharp
if (!string.IsNullOrWhiteSpace(ToolDisplay)) parts.Add(ToolDisplay!);
```

In `ChatView.xaml.cs` `ApplyAgentActivity`: remove the early Thinking Completed delete block that removes the item and returns. Keep in-place update by id. Add a `StartNewAgentActivityRun` method called when a new run starts that clears `_agentActivityById` and `AgentActivities` and collapses the feed. Do not clear on terminal completion, cancellation, or failure. Call the clear method from the existing run-start path only.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --nologo --filter "FullyQualifiedName~AgentActivityItemTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add gui-winui3/Models/AgentActivityItem.cs gui-winui3/Views/ChatView.xaml.cs gui-winui3.Tests/AgentActivityItemTests.cs
git commit -m "feat(agent-activity): retain per-run timeline with tool labels"
```

---

## Final verification

Run: `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --nologo`
Run: `dotnet test backend-dotnet/RuAgent.Harness.Core.Tests/RuAgent.Harness.Core.Tests.csproj --nologo`
Run: `dotnet test backend-dotnet/RuAgent.Client.Tests/RuAgent.Client.Tests.csproj --nologo`
Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --nologo`
Run: `python -m pytest -q`
Expected: all PASS before handoff.
