using System.Text;
using System.Text.Json;
using RuAgent.Cli;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// T5b keyboard semantics for the persistent footer: between turns Enter hands the line to
/// the idle reader, during a turn it queues, and pickers/secrets/approvals own the keyboard
/// as panels. Pure key routing, no console.
/// </summary>
public sealed class TuiIdleComposerTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key, char ch, bool control = false) =>
        new(ch, key, false, false, control);

    private static (TypeAheadSession Session, TaskCompletionSource<string?> Idle) NewIdleSession()
    {
        var session = new TypeAheadSession(new PendingPromptSlot()) { TurnActive = false };
        var idle = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.IdleLine = idle;
        return (session, idle);
    }

    [Fact]
    public async Task IdleEnter_HandsTheLineToTheIdleReader()
    {
        var (session, idle) = NewIdleSession();
        session.Buffer = "explain the approve flow";

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Enter, '\r'));

        Assert.Equal("explain the approve flow", await idle.Task);
        Assert.Empty(session.Buffer);
    }

    [Fact]
    public void TurnEnter_QueuesInsteadOfResolvingTheIdleReader()
    {
        var (session, idle) = NewIdleSession();
        session.TurnActive = true;
        session.Buffer = "while you work";

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Enter, '\r'));

        Assert.False(idle.Task.IsCompleted);
        Assert.Equal("while you work", session.Queue.Value);
    }

    [Fact]
    public async Task IdleEmptyEnter_DoesNothing()
    {
        var (session, idle) = NewIdleSession();
        session.Buffer = "   ";

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Enter, '\r'));

        Assert.False(idle.Task.IsCompleted);
        Assert.False(session.Queue.HasValue);
    }

    [Theory]
    [InlineData('1')]
    [InlineData('2')]
    [InlineData('3')]
    public async Task ChoicePanel_DigitQuickPick_ResolvesTheValue(char digit)
    {
        var session = new TypeAheadSession(new PendingPromptSlot());
        var list = new ChoiceList(
            "reasoning",
            [
                new ChoiceItem("low", string.Empty, "low"),
                new ChoiceItem("medium", string.Empty, "medium"),
                new ChoiceItem("high", string.Empty, "high"),
            ],
            selected: 1);
        var request = new PendingChoiceRequest(list);
        session.Choice = request;

        InteractiveLineReader.HandleTypeAheadKey(session, Key((ConsoleKey)digit, digit));

        Assert.Equal(digit - '0', list.Selected + 1);
        Assert.Equal(list.Current.Value, await request.Decision.Task);
    }

    [Fact]
    public async Task ChoicePanel_EscapeCancels()
    {
        var session = new TypeAheadSession(new PendingPromptSlot());
        var request = new PendingChoiceRequest(new ChoiceList("backend", [new ChoiceItem("openai", string.Empty, "openai")]));
        session.Choice = request;

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Escape, '\u001b'));

        Assert.Null(await request.Decision.Task);
    }

    [Fact]
    public async Task SecretPanel_TypesMasksAndResolvesWithoutTouchingTheComposer()
    {
        var session = new TypeAheadSession(new PendingPromptSlot()) { Buffer = "draft stays" };
        var request = new PendingSecretRequest("OpenAI");
        session.Secret = request;

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.S, 's'));
        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.K, 'k'));
        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Backspace, '\b'));
        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Enter, '\r'));

        Assert.Equal("s", await request.Decision.Task);
        Assert.Equal("draft stays", session.Buffer);
        Assert.Empty(session.Queue.Value ?? string.Empty);
    }

    [Fact]
    public async Task SecretPanel_EscapeReturnsNull()
    {
        var session = new TypeAheadSession(new PendingPromptSlot());
        var request = new PendingSecretRequest(null);
        session.Secret = request;
        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.A, 'a'));

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Escape, '\u001b'));

        Assert.Null(await request.Decision.Task);
    }

    [Fact]
    public async Task ApprovalPanel_DigitQuickPick_ResolvesTheDecision()
    {
        var session = new TypeAheadSession(new PendingPromptSlot());
        var approval = ApprovalPrompt.For("terminal_delete_requires_approval");
        session.Approval = approval;

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.D3, '3'));

        Assert.Equal(ApprovalPrompt.AllowAlways, await approval.Decision.Task);
    }

    [Fact]
    public void TranscriptFullScreenCards_RendersInlineToolRowAndExpandsTheDiff()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var writer = new StringWriter();
        var renderer = new TuiSessionRenderer(writer) { Width = 80 };
        renderer.Transcript.FullScreenCards = true;
        renderer.BeginTurn();

        renderer.RenderFrame(Activity("edit", "started", 1, "app.cs", "Edit"));
        renderer.RenderFrame(Durable(
            "tool.execution_completed", "succeeded",
            "@@ -1 +1 @@\n- old line\n+ new line", 2));

        var text = writer.ToString();
        Assert.Contains("← Edit app.cs", TuiText.Strip(text), StringComparison.Ordinal);
        Assert.Contains("ctrl+o", text, StringComparison.Ordinal);
        Assert.DoesNotContain("╭─", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- old line", text, StringComparison.Ordinal);

        renderer.ToggleExpand();
        text = writer.ToString();
        Assert.Contains("- old line", text, StringComparison.Ordinal);
        Assert.Contains("+ new line", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptFullScreenCards_HighlightsFencedCode()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var writer = new StringWriter();
        var renderer = new TuiSessionRenderer(writer) { Width = 80 };
        renderer.Transcript.FullScreenCards = true;
        renderer.BeginTurn();

        renderer.RenderFrame(Delta("```cs\nvar x = 1;\n```\n", 1));
        renderer.Flush();

        Assert.Contains("  1 │ var x = 1;", writer.ToString(), StringComparison.Ordinal);
    }

    private static SseFrame Activity(
        string kind, string state, long sequence, string? fileName = null, string? toolDisplay = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["activity_id"] = "act_" + new string('b', 32),
            ["kind"] = kind,
            ["state"] = state,
            ["safe_code"] = "succeeded",
        };
        if (fileName is not null) payload["file_name"] = fileName;
        if (toolDisplay is not null) payload["tool_display"] = toolDisplay;
        var outer = JsonSerializer.Serialize(new { kind = "agent.activity", data = JsonSerializer.Serialize(payload) });
        return new SseFrame(null, "transient", outer, true, sequence);
    }

    private static SseFrame Delta(string text, long sequence)
    {
        var inner = JsonSerializer.Serialize(new { text, model_step_id = "step_" + new string('a', 32) });
        var outer = JsonSerializer.Serialize(new { kind = "assistant.delta", data = inner });
        return new SseFrame(null, "transient", outer, true, sequence);
    }

    private static SseFrame Durable(string eventType, string status, string safeSummary, long sequence) =>
        new(
            sequence,
            "message",
            JsonSerializer.Serialize(new HarnessSafeEventEnvelope(
                ProtocolVersion: 1,
                EventId: "evt_" + sequence,
                RunId: "run_" + new string('c', 32),
                SessionId: "sess_1",
                Sequence: sequence,
                EventType: eventType,
                OccurredAt: "2026-10-02T00:00:00Z",
                Status: status,
                SafeSummary: safeSummary)),
            false,
            null);
}
