# Grok-style TUI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Nâng cấp `agent chat` sang phong cách Grok TUI (header card, status bar, `◆` thought, tool compact, permission card, turn-end, prompt legend) + expose số usage thật qua SSE safe envelope.

**Architecture:** Protocol thêm 3 fields optional (giữ version 1) → `RunUsageTracker` mới trong RuBackend cộng dồn per-request trong `StreamDurablesAsync` (điểm enrich duy nhất, cover cả replay + live) → CLI render bằng Spectre cho pieces tĩnh, giữ key-loop/streaming hiện tại.

**Tech Stack:** .NET 10, C#, xUnit, Spectre.Console 0.57.2 (pin), ASP.NET Core SSE.

**Spec:** `docs/superpowers/specs/2026-09-09-grok-style-tui-design.md`

## Global Constraints

- `HarnessSafeEventEnvelope.CurrentProtocolVersion` giữ `1`; 3 fields mới nullable, default `null`.
- Không đổi Worker/provider, policy/approval logic, privacy/redaction rules.
- Cumulative semantics: `total_tokens` cộng dồn per run; `input/output_tokens` của step hiện tại; thiếu field nào thì cộng đúng field đó, không suy diễn.
- Mọi enrich fail → `null` (fail-closed), stream không gián đoạn, không log payload bytes.
- Conventional Commits có scope (`feat(protocol):`, `feat(backend):`, `feat(cli):`, `test(...):`).
- Mỗi task xong chạy test liên quan; cuối plan chạy `.\scripts\test_dotnet.ps1 -Configuration Debug`.

---

### Task 1: Protocol — thêm 3 usage fields vào safe envelope

**Files:**
- Modify: `backend-dotnet/RuAgent.Protocol/Harness/HarnessEventContracts.cs:5-16`
- Test: `backend-dotnet/RuAgent.Protocol.Tests/Harness/HarnessSafeEventEnvelopeTests.cs` (mới, theo convention folder `Harness/`)

**Interfaces:**
- Consumes: không có (task đầu).
- Produces: `HarnessSafeEventEnvelope(..., long? InputTokens = null, long? OutputTokens = null, long? TotalTokens = null)` — hỗ trợ `with` expression; các task sau dùng 3 props này.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Envelope_RoundTrips_UsageFields()
{
    var envelope = new HarnessSafeEventEnvelope(
        ProtocolVersion: HarnessSafeEventEnvelope.CurrentProtocolVersion,
        EventId: "evt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        RunId: "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        SessionId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        Sequence: 9,
        EventType: "model.step_completed",
        OccurredAt: "2026-09-09T00:00:00Z",
        Status: "running",
        SafeSummary: "step done",
        InputTokens: 1200,
        OutputTokens: 300,
        TotalTokens: 1500);

    var json = JsonSerializer.Serialize(envelope);
    var back = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(json)!;

    Assert.Equal(1200, back.InputTokens);
    Assert.Equal(300, back.OutputTokens);
    Assert.Equal(1500, back.TotalTokens);
    Assert.Equal(1, HarnessSafeEventEnvelope.CurrentProtocolVersion);
}

[Fact]
public void Envelope_WithoutUsage_ParsesOldJson()
{
    var oldJson = """{"protocol_version":1,"event_id":"evt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","run_id":"run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","session_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","sequence":9,"event_type":"model.step_completed","occurred_at":"2026-09-09T00:00:00Z","status":"running","safe_summary":"step done"}""";
    var back = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(oldJson)!;

    Assert.Null(back.InputTokens);
    Assert.Null(back.OutputTokens);
    Assert.Null(back.TotalTokens);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Protocol.Tests --filter "FullyQualifiedName~Envelope_RoundTrips_UsageFields"`
Expected: FAIL (compile error — `InputTokens` chưa tồn tại).

- [ ] **Step 3: Write minimal implementation**

```csharp
public sealed record HarnessSafeEventEnvelope(
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("occurred_at")] string OccurredAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("safe_summary")] string SafeSummary,
    [property: JsonPropertyName("payload_ref")] string? PayloadRef = null,
    [property: JsonPropertyName("approval_id")] string? ApprovalId = null,
    [property: JsonPropertyName("input_tokens")] long? InputTokens = null,
    [property: JsonPropertyName("output_tokens")] long? OutputTokens = null,
    [property: JsonPropertyName("total_tokens")] long? TotalTokens = null)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Protocol.Tests --filter "FullyQualifiedName~HarnessSafeEventEnvelope"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Protocol backend-dotnet/RuAgent.Protocol.Tests
git commit -m "feat(protocol): add optional usage fields to safe envelope"
```

---

### Task 2: Backend — `RunUsageTracker` + `TryReadModelUsageAsync`

**Files:**
- Create: `backend-dotnet/RuBackend/Harness/RunUsageTracker.cs`
- Modify: `backend-dotnet/RuBackend/Harness/AgentV1Service.Runs.cs` (thêm method mới, không sửa method cũ)
- Test: `backend-dotnet/RuBackend.Tests/Harness/RunUsageTrackerTests.cs` (mới)

**Interfaces:**
- Consumes: `RuAgent.Harness.Core.ModelUsage` (`int? InputTokens/OutputTokens/TotalTokens`, `ModelUsage.Read(JsonElement)`), `HarnessPayloadContent` (`Plaintext`, `Encoding`), `_payloadStore.ReadForClientAsync(runId, payloadId, ct)`.
- Produces: `RunUsageTracker.Track(ModelUsage?)` + `RunUsageTracker.Snapshot() → (long? InputTokens, long? OutputTokens, long? TotalTokens)`; `AgentV1Service.TryReadModelUsageAsync(string runIdRaw, string payloadIdRaw, CancellationToken) → Task<ModelUsage?>`. Task 3 dùng cả hai.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Tracker_Accumulates_PerField_And_Snapshots()
{
    var tracker = new RunUsageTracker();
    Assert.Equal((null, null, null), SnapshotOf(tracker));

    tracker.Track(new ModelUsage(inputTokens: 1000, outputTokens: 200, totalTokens: 1200, cacheReadInputTokens: null, cacheWriteInputTokens: null));
    tracker.Track(new ModelUsage(inputTokens: 500, outputTokens: null, totalTokens: null, cacheReadInputTokens: null, cacheWriteInputTokens: null));

    var (input, output, total) = SnapshotOf(tracker);
    Assert.Equal(1500, input);
    Assert.Equal(200, output);
    Assert.Equal(1200, total);
}

[Fact]
public void Tracker_Ignores_Null_Usage()
{
    var tracker = new RunUsageTracker();
    tracker.Track(null);
    Assert.Equal((null, null, null), SnapshotOf(tracker));
}

private static (long?, long?, long?) SnapshotOf(RunUsageTracker tracker)
{
    var s = tracker.Snapshot();
    return (s.InputTokens, s.OutputTokens, s.TotalTokens);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuBackend.Tests --filter "FullyQualifiedName~RunUsageTracker"`
Expected: FAIL (compile error — class chưa tồn tại).

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace RuBackend.Harness;

/// <summary>
/// Cộng dồn usage per SSE request. Thiếu field nào thì cộng đúng field đó,
/// không suy diễn. Chưa có số nào thì Snapshot trả null.
/// </summary>
public sealed class RunUsageTracker
{
    private long _input;
    private long _output;
    private long _total;
    private bool _hasInput;
    private bool _hasOutput;
    private bool _hasTotal;

    public void Track(RuAgent.Harness.Core.ModelUsage? usage)
    {
        if (usage is null) return;
        if (usage.InputTokens is { } input) { _input += input; _hasInput = true; }
        if (usage.OutputTokens is { } output) { _output += output; _hasOutput = true; }
        if (usage.TotalTokens is { } total) { _total += total; _hasTotal = true; }
    }

    public (long? InputTokens, long? OutputTokens, long? TotalTokens) Snapshot() =>
        (_hasInput ? _input : null, _hasOutput ? _output : null, _hasTotal ? _total : null);
}
```

Service method (thêm vào partial `AgentV1Service`, cạnh `GetPayloadAsync`):

```csharp
public async Task<RuAgent.Harness.Core.ModelUsage?> TryReadModelUsageAsync(string runIdRaw, string payloadIdRaw, CancellationToken cancellationToken)
{
    try
    {
        if (!IsRunId(runIdRaw) || !IsPayloadId(payloadIdRaw)) return null;
        var content = await _payloadStore.ReadForClientAsync(new RunId(runIdRaw), new PayloadId(payloadIdRaw), cancellationToken).ConfigureAwait(false);
        if (content.Encoding == HarnessPayloadEncoding.Opaque) return null;
        if (content.Plaintext.Length > 4096) return null;
        using var doc = JsonDocument.Parse(content.Plaintext);
        return RuAgent.Harness.Core.ModelUsage.Read(doc.RootElement);
    }
    catch
    {
        return null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuBackend.Tests --filter "FullyQualifiedName~RunUsageTracker"`
Expected: PASS. Rồi build backend: `dotnet build backend-dotnet/RuBackend/RuBackend.csproj`

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuBackend backend-dotnet/RuBackend.Tests
git commit -m "feat(backend): add run usage tracker and usage payload reader"
```

---

### Task 3: Backend — wire enrich vào `StreamDurablesAsync` + endpoint test

> Thực tế implement: drive-run qua HTTP factory fail trên máy dev do
> `WindowsDpapiKeyStore.HardenDirectory` (ổ D:) và ổ C: đầy — test gốc cũng
> fail y hệt nên chuyển sang seed events trực tiếp qua `IHarnessEventStore`
> + GET SSE (không drive). Phát hiện thêm bug production: `Policies` thiếu
> `ModelUsage` nên mọi commit usage payload đều throw `payload_kind_invalid`
> (cũng là nguyên nhân run failed bí ẩn ở final step) — đã fix trong task này.

**Files:**
- Modify: `backend-dotnet/RuBackend/Harness/AgentV1Endpoints.cs:298-333` (trong `StreamDurablesAsync`)
- Test: `backend-dotnet/RuBackend.Tests/Harness/` — thêm test SSE endpoint mới `AgentUsageEnrichmentTests.cs`, reuse pattern `ReadEventsAsync(client, runId)` (GET `/agent/v1/runs/{runId}/events?live=0`, parse dòng `data: ` chứa `"event_type"`) và `ScriptedRunner : IModelStepRunner` như `AgentApprovalEndpointTests.cs:601-690`.

**Interfaces:**
- Consumes: `RunUsageTracker`, `TryReadModelUsageAsync` (Task 2); `HarnessEventMapper.ToSafeEnvelope(evt, run)`; event kind string `"model.usage_recorded"`.
- Produces: SSE durable JSON có `input_tokens/output_tokens/total_tokens` sau khi run có usage. Task 6 (CLI) consume.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Sse_Envelopes_Carry_Usage_After_ModelUsageRecorded()
{
    // Arrange: copy nguyên fixture class (WebApplicationFactory, client, helpers
    // ReadEventsAsync/WaitForStatusAsync/GetStatusAsync/CreateRun) từ
    // AgentApprovalEndpointTests.cs sang file mới, đổi tên class; chỉ khác ở
    // ScriptedRunner: trả 1 step kèm usage:
    //   new AssistantStep("done", Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 0,
    //       usage: new ModelUsage(inputTokens: 1000, outputTokens: 200, totalTokens: 1200,
    //           cacheReadInputTokens: null, cacheWriteInputTokens: null))
    // (Coordinator tự sinh event "model.usage_recorded" từ step.Usage.)
    var runId = await CreateScriptedRunWithUsageAsync(client);
    var events = await ReadEventsAsync(client, runId);

    var completed = events.Where(e => e.GetProperty("event_type").GetString() == "model.step_completed").ToList();
    Assert.NotEmpty(completed);
    var last = completed[^1];
    Assert.Equal(1200, last.GetProperty("total_tokens").GetInt64());
}

[Fact]
public async Task Sse_Envelopes_Omit_Usage_Keys_When_Run_Has_No_Usage()
{
    // Run scripted KHÔNG usage → enrich không gắn gì → keys vắng mặt hoàn toàn
    // (code chỉ `with` khi tracker có ít nhất 1 số), fail-closed ở stream-level.
    var runId = await CreateScriptedRunWithoutUsageAsync(client);
    var events = await ReadEventsAsync(client, runId);

    Assert.NotEmpty(events);
    foreach (var e in events)
    {
        Assert.False(e.TryGetProperty("total_tokens", out _));
        Assert.False(e.TryGetProperty("input_tokens", out _));
        Assert.False(e.TryGetProperty("output_tokens", out _));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuBackend.Tests --filter "FullyQualifiedName~Sse_Envelopes_Carry_Usage"`
Expected: FAIL (`total_tokens` missing/null).

- [ ] **Step 3: Write minimal implementation**

Trong `StreamDurablesAsync` (trước `await foreach`), thêm:

```csharp
var usageTracker = new RunUsageTracker();
```

Trong loop, sau khi có `envelope.Event`, trước `ToSafeEnvelope`:

```csharp
if (string.Equals(envelope.Event.Kind, "model.usage_recorded", StringComparison.Ordinal)
    && envelope.Event.PayloadId is { } usagePayloadId)
{
    var usage = await service.TryReadModelUsageAsync(runId, usagePayloadId.Value, ct).ConfigureAwait(false);
    usageTracker.Track(usage);
}
```

Sau `var safe = HarnessEventMapper.ToSafeEnvelope(envelope.Event, run);`, thêm:

```csharp
var (usedInput, usedOutput, usedTotal) = usageTracker.Snapshot();
if (usedInput is not null || usedOutput is not null || usedTotal is not null)
{
    safe = safe with { InputTokens = usedInput, OutputTokens = usedOutput, TotalTokens = usedTotal };
}
```

Không đổi gì khác trong loop: serialize → `ContainsForbiddenField` check → `WriteDurableAsync` giữ nguyên (3 keys mới không nằm trong forbidden list nên pass).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuBackend.Tests --filter "FullyQualifiedName~AgentUsageEnrichment"`
Expected: PASS. Rồi chạy cả folder Harness tests để không vỡ regression.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuBackend backend-dotnet/RuBackend.Tests
git commit -m "feat(backend): enrich SSE envelopes with cumulative run usage"
```

---

### Task 4: CLI — Spectre package + `GrokHeader`

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj` (thêm 1 PackageReference)
- Create: `backend-dotnet/RuAgent.Cli/Tui/GrokHeader.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/GrokHeaderTests.cs` (mới)

**Interfaces:**
- Consumes: `Spectre.Console` 0.57.2 (`Panel`, `IAnsiConsole`, `AnsiConsole.Create(AnsiConsoleSettings)`); `AnsiTheme.IsEnabled` cho fallback.
- Produces: `GrokHeader.Render(IAnsiConsole console, string sessionId, string workspace, string backend, string model, string preset)`. Task 7 gọi trong chat loop.

- [ ] **Step 1: Add the package (setup cho task này)**

Run: `dotnet add backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj package Spectre.Console --version 0.57.2`
Verify: `dotnet list backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj package` hiện `Spectre.Console 0.57.2`. Nếu restore fail (offline) → DỪNG, báo chủ nhân (không fallback lén package khác).

- [ ] **Step 2: Write the failing test**

```csharp
[Fact]
public void GrokHeader_Renders_Session_Workspace_Model()
{
    using var writer = new StringWriter();
    var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = writer });
    GrokHeader.Render(console, "sess_12345678", "D:\\repo", "Kilo Gateway", "grok-4.5", "standard");
    var output = writer.ToString();

    Assert.Contains("RuAgent", output, StringComparison.Ordinal);
    Assert.Contains("sess_12345678", output, StringComparison.Ordinal);
    Assert.Contains("D:\\repo", output, StringComparison.Ordinal);
    Assert.Contains("grok-4.5", output, StringComparison.Ordinal);
}

[Fact]
public void GrokHeader_FallsBack_To_PlainText_When_Theme_Disabled()
{
    AnsiTheme.IsEnabled = false;
    try
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = writer });
        GrokHeader.Render(console, "sess_1", "D:\\repo", "Kilo Gateway", "m", "standard");
        Assert.Contains("sess_1", writer.ToString(), StringComparison.Ordinal);
    }
    finally
    {
        AnsiTheme.IsEnabled = true;
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests --filter "FullyQualifiedName~GrokHeader"`
Expected: FAIL (compile error — class chưa tồn tại).

- [ ] **Step 4: Write minimal implementation**

```csharp
using Spectre.Console;

namespace RuAgent.Cli.Tui;

public static class GrokHeader
{
    public static void Render(IAnsiConsole console, string sessionId, string workspace, string backend, string model, string preset)
    {
        ArgumentNullException.ThrowIfNull(console);
        if (!AnsiTheme.IsEnabled)
        {
            console.WriteLine($"RuAgent · Session: {sessionId} · Workspace: {workspace} · {backend}/{model} · {preset}");
            return;
        }

        var body = new Markup(
            $"[bold]RuAgent[/] [dim]v2.0 · Autonomous Coding Agent[/]\n" +
            $"[dim]Session:[/] [yellow]{Markup.Escape(sessionId)}[/]\n" +
            $"[dim]Workspace:[/] {Markup.Escape(workspace)}\n" +
            $"[dim]Backend · Model:[/] {Markup.Escape(backend)} · {Markup.Escape(model)} [dim]({Markup.Escape(preset)})[/]\n" +
            $"[dim]/model /fork /rewind /stats /export /history /exit[/]");
        console.Write(new Panel(body) { Header = new PanelHeader(" [cyan]❯ RuAgent[/] "), Border = BoxBorder.Rounded, Expand = true });
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests --filter "FullyQualifiedName~GrokHeader"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add backend-dotnet/RuAgent.Cli backend-dotnet/RuAgent.Cli.Tests
git commit -m "feat(cli): add Spectre-based Grok header card"
```

---

### Task 5: CLI — `GrokStatusBar`

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/GrokStatusBar.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/GrokStatusBarTests.cs` (mới)

**Interfaces:**
- Consumes: `Spectre.Console` (`Rule`, `IAnsiConsole`); `AnsiTheme.IsEnabled`.
- Produces: `GrokStatusBar.Render(IAnsiConsole console, string workspace, string backend, string model, string preset, int turn, long? totalTokens)`. Task 7 gọi mỗi turn.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void StatusBar_Shows_Turn_And_Total_Tokens()
{
    using var writer = new StringWriter();
    var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = writer });
    GrokStatusBar.Render(console, "D:\\repo", "Kilo Gateway", "grok-4.5", "standard", 3, 1500);
    var output = writer.ToString();

    Assert.Contains("turn 3", output, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("1500", output, StringComparison.Ordinal);
    Assert.Contains("grok-4.5", output, StringComparison.Ordinal);
}

[Fact]
public void StatusBar_Hides_Token_Part_When_Null()
{
    using var writer = new StringWriter();
    var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = writer });
    GrokStatusBar.Render(console, "D:\\repo", "Kilo Gateway", "grok-4.5", "standard", 1, null);
    var output = writer.ToString();

    Assert.Contains("turn 1", output, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("⇣", output, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests --filter "FullyQualifiedName~GrokStatusBar"`
Expected: FAIL (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
using Spectre.Console;

namespace RuAgent.Cli.Tui;

public static class GrokStatusBar
{
    public static void Render(IAnsiConsole console, string workspace, string backend, string model, string preset, int turn, long? totalTokens)
    {
        ArgumentNullException.ThrowIfNull(console);
        var tokens = totalTokens is { } t ? $" · ⇣{t}" : string.Empty;
        var plain = $"{workspace} · {backend}/{model} · {preset} · turn {turn}{tokens}";
        if (!AnsiTheme.IsEnabled)
        {
            console.WriteLine(plain);
            return;
        }

        console.Write(new Rule($"[dim]{Markup.Escape(workspace)} · {Markup.Escape(backend)}/{Markup.Escape(model)} · {Markup.Escape(preset)} · turn {turn}[/]{(totalTokens is { } tt ? $" [yellow]⇣{tt}[/]" : string.Empty)}"));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests --filter "FullyQualifiedName~GrokStatusBar"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli backend-dotnet/RuAgent.Cli.Tests
git commit -m "feat(cli): add Grok status bar with turn and token totals"
```

---

### Task 6: CLI — restyle `TuiEventRenderer` (thought/tool/permission/turn-end)

> Bổ sung khi implement: production trả status `"completed"` (không phải
> `"succeeded"`) nên điều kiện turn-end accept cả hai; `NdjsonEventRenderer`
> emit thêm 3 số usage (allowlist); tắt test parallel cho `Cli.Tests` vì
> `AnsiTheme.IsEnabled` static không thread-safe.

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiEventRenderer.cs`, `backend-dotnet/RuAgent.Cli/Tui/AnsiTheme.cs` (thêm palette nếu cần)
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiInteractiveTests.cs` (update expectations cũ), `TerminalEventRendererTests.cs` (không đổi hành vi privacy), thêm `GrokRendererTests.cs` cho pieces mới

**Interfaces:**
- Consumes: envelope `InputTokens/OutputTokens/TotalTokens` (Task 1); working-line dùng `\r` pattern có sẵn của spinner.
- Produces: style Grok cho thought/tool/permission/turn-end; Task 7 không cần gì thêm.

Thay đổi render (giữ nguyên logic parse/filter/redact):

| Event | Cũ | Mới |
|---|---|---|
| thinking spinner | cyan `Thinking... (s)` | amber `⠋ Thinking… (s)` |
| thought done | dim `• Thought for Xs` | `◆ Thought for Xs` |
| tool request | `⚙ [Tool Request #]` + emoji | `read <path>` / `edit <file>` / compact verb, card `┃ ◆` cho detail |
| tool done | `✓ 📖 [Tool Done #]` | `wrote <file> +<n>` / `✓ read <path>`; diff giữ `FormatDiffLine`, thêm số dòng gutter `┃` |
| approval | box `╔═╗` + keys text | Spectre `Panel` viền trái + radio `(●)/(○)` + hint bar; phím `y/r/n` giữ nguyên |
| run completed | `└─ [Run # Succeeded]` | `◆stop` + `Turn completed in Xs` (elapsed từ `_thinkingWatch`-style stopwatch per turn) |
| working line (mới) | không có | `⠋ Waiting for response… {s}s ⇣{total} [stop]` bằng `\r`; `TotalTokens null` → ẩn `⇣` |

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Renderer_Renders_Grok_Thought_And_Turn_End()
{
    AnsiTheme.IsEnabled = false;
    using var sw = new StringWriter();
    TuiEventRenderer.RenderEnvelope(Envelope("model.turn_started", "x", sequence: 4), sw);
    // Style mới bắt buộc có ◆ : `┌─ ◆ Turn #4` (code cũ `┌─ Turn #4: ...` không có ◆ nên FAIL trước khi sửa)
    Assert.Contains("◆", sw.ToString(), StringComparison.Ordinal);
    Assert.Contains("Turn #4", sw.ToString(), StringComparison.Ordinal);

    using var sw2 = new StringWriter();
    TuiEventRenderer.RenderEnvelope(Envelope("run.completed", "done", sequence: 9, status: "succeeded"), sw2);
    Assert.Contains("Turn completed in", sw2.ToString(), StringComparison.Ordinal);
}

[Fact]
public void Renderer_Renders_Compact_Tool_Lines()
{
    AnsiTheme.IsEnabled = false;
    using var sw = new StringWriter();
    TuiEventRenderer.RenderEnvelope(Envelope("tool.execution_requested", "read op=read path: src/app.cs", sequence: 10), sw);
    var output = sw.ToString();
    Assert.Contains("read", output, StringComparison.Ordinal);
    Assert.DoesNotContain("📖", output, StringComparison.Ordinal);
    Assert.DoesNotContain("[Tool Request #10]", output, StringComparison.Ordinal);
}

[Fact]
public void Renderer_Working_Line_Hides_Tokens_When_Null()
{
    // envelope không có usage (server cũ) → không crash, không hiện ⇣
    var envelope = Envelope("model.step_completed", "step done", sequence: 5);
    Assert.Null(envelope.TotalTokens);
    using var sw = new StringWriter();
    TuiEventRenderer.RenderEnvelope(envelope, sw);
    Assert.DoesNotContain("⇣", sw.ToString(), StringComparison.Ordinal);
}
```

(Hàm `Envelope(...)` helper đã có trong `TerminalEventRendererTests.cs:10-25` — copy pattern sang file mới, không sửa helper cũ.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests --filter "FullyQualifiedName~GrokRenderer|FullyQualifiedName~TuiEventRenderer_Renders"`
Expected: FAIL (style cũ chưa khớp).

- [ ] **Step 3: Write minimal implementation**

Sửa `TuiEventRenderer.cs` theo bảng mapping trên. Quy tắc:
- Không đổi `RenderFrame` parse logic, transient filter, `Escape` bound 500 chars, diff detect.
- `model.turn_started` render `┌─ ◆ Turn #{seq}` (thay `┌─ Turn #{seq}: Model Thinking...`).
- Turn elapsed: thêm field `Stopwatch _turnWatch`, `Start()` ở nhánh `model.turn_started`, đọc `Elapsed` ở nhánh `run.completed/run.failed` → `◆stop` + `Turn completed in {s:F1}s`. Ctrl+C giữa chừng: behavior dọn `\r` + POST cancel giữ nguyên.
- Approval: thay box `╔═╗` bằng Spectre `Panel` — nhưng renderer nhận `TextWriter`, không phải `IAnsiConsole`. Cách nối: tạo console tạm `AnsiConsole.Create(new AnsiConsoleSettings { Out = _writer })` trong nhánh approval khi `AnsiTheme.IsEnabled`, else giữ text fallback. (Spectre viết vào cùng `_writer` nên thứ tự output giữ nguyên.) Spectre `Panel` tự wrap theo console width nên không cần clamp tay 48..76 như box cũ.
- Working line: working-line render khi nhận `model.turn_started`/`tool.execution_requested` (static line), elapsed cập nhật ở `Turn completed`. Không thêm thread mới. (YAGNI: bỏ live-elapsed, giữ static + tổng kết.)
- Update expectations cũ trong `TuiInteractiveTests.cs:82` (`"Tool Request #2"` → style mới), `:218-219` (`✏️`, `Tool Request #10`), `:235` (`✓ ✏️ [Tool Done #11]`).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests`
Expected: PASS toàn bộ Cli.Tests.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli backend-dotnet/RuAgent.Cli.Tests
git commit -m "feat(cli): restyle event renderer in Grok style"
```

---

### Task 7: CLI — wire header/prompt/status vào chat loop + smoke test

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.cs:775-826` (banner box + prompt), vòng loop (thêm turn counter + status bar + working summary)
- Test: manual smoke (không thêm unit test cho loop tương tác; đã có coverage pieces ở Task 4-6)

**Interfaces:**
- Consumes: `GrokHeader.Render`, `GrokStatusBar.Render` (Task 4-5); model/backend từ `SettingsApiClient.GetAsync` (pattern có sẵn như `HandleModelAsync`).
- Produces: chat loop Grok-style hoàn chỉnh.

Thay đổi:

1. Thay banner box `╭─╮` (dòng 775-817) bằng `GrokHeader.Render(AnsiConsole.Console, sessionId, canonicalWorkspace, backend, model, preset)` — lấy backend/model bằng 1 lần `SettingsApiClient.GetAsync` trước loop (reuse switch backend→model field của `HandleModelAsync`).
2. Prompt: `ruagent ❯ ` → `❯ {model} · {preset} ` (giữ `Bold+Cyan`), cont prompt giữ `... ❯ `. Sau mỗi prompt in 1 dòng legend dim: `\ xuống dòng · Tab complete · Up/Down history · Ctrl+C hủy`.
3. Turn counter `int turn = 0` tăng mỗi prompt user gửi; sau mỗi turn render `GrokStatusBar.Render(..., turn, lastTotalTokens)` — `lastTotalTokens` cập nhật khi envelope có `TotalTokens` (track trong loop render, biến local).
4. Không đổi slash handling, approval keys, Ctrl+C cancel, `--json` path.

- [ ] **Step 1: Implement theo 4 điểm trên (không có test mới — pieces đã cover)**

- [ ] **Step 2: Build Release CLI**

Run: `dotnet build backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj -c Release`
Expected: build thành công, warning mới = 0.

- [ ] **Step 3: Manual smoke**

Run: `dotnet run --project backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj -- agent chat --workspace D:/LLM` rồi: header card hiện đúng → gửi 1 prompt ngắn → thought `◆` + tool compact + `Turn completed` → số `⇣` ở status bar khớp `/stats` (cùng semantics cumulative) → approval `y` 1 lần → Ctrl+C giữa turn → `/exit`. Ghi kết quả từng điểm (pass/fail); nếu fail → sửa, không commit.

- [ ] **Step 4: Commit (chỉ khi smoke pass)**

```bash
git add backend-dotnet/RuAgent.Cli
git commit -m "feat(cli): wire Grok header prompt and status bar into chat loop"
```

---

### Task 8: Full gates + privacy regression

**Files:** không đổi code (chỉ fix nếu gate đỏ).

- [ ] **Step 1: Chạy full .NET gates**

Run: `.\scripts\test_dotnet.ps1 -Configuration Debug`
Expected: 13 projects pass, dừng ở lỗi đầu tiên → fix rồi chạy lại.

- [ ] **Step 2: Privacy regression check**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests --filter "FullyQualifiedName~Privacy"` + `dotnet test backend-dotnet/RuBackend.Tests --filter "FullyQualifiedName~Privacy"`
Expected: PASS — Ndjson chỉ thêm 3 số usage, không leak `prompt/command/path/output`.

- [ ] **Step 3: Cập nhật spec trạng thái (nếu có lệch implement)**

Nếu implement lệch spec (tên field, semantics), sửa spec file cho khớp code (docs sync với code theo luật repo), commit riêng:

```bash
git add docs/superpowers/specs/2026-09-09-grok-style-tui-design.md
git commit -m "docs(spec): sync Grok TUI spec with implementation"
```
