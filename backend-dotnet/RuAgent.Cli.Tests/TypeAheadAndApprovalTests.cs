using RuAgent.Cli;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ApprovalPromptTests
{
    private static ApprovalPrompt Create(bool offerDirectory = true) =>
        ApprovalPrompt.For("external_directory_required", offerDirectory);

    [Fact]
    public void For_OffersTheThreeWayChoiceWithDenySelected()
    {
        var prompt = Create();

        Assert.Equal(3, prompt.Choices.Count);
        Assert.Equal(ApprovalPrompt.AllowOnce, prompt.Choices[0].Decision);
        Assert.Equal(ApprovalPrompt.Deny, prompt.Choices[1].Decision);
        Assert.Equal(ApprovalPrompt.AllowDirectory, prompt.Choices[2].Decision);
        Assert.Equal(ApprovalPrompt.Deny, prompt.Current.Decision);
    }

    [Fact]
    public void For_WithoutDirectorySupport_OffersTwoChoices()
    {
        var prompt = Create(offerDirectory: false);

        Assert.Equal(2, prompt.Choices.Count);
        Assert.DoesNotContain(prompt.Choices, c => c.Decision == ApprovalPrompt.AllowDirectory);
    }

    [Fact]
    public void Move_WalksAndWrapsTheChoices()
    {
        var prompt = Create();
        Assert.Equal(ApprovalPrompt.Deny, prompt.Current.Decision);

        prompt.Move(-1);
        Assert.Equal(ApprovalPrompt.AllowOnce, prompt.Current.Decision);

        prompt.Move(-1);
        Assert.Equal(ApprovalPrompt.AllowDirectory, prompt.Current.Decision);

        prompt.Move(1);
        Assert.Equal(ApprovalPrompt.AllowOnce, prompt.Current.Decision);
    }

    [Theory]
    [InlineData('1', ApprovalPrompt.AllowOnce)]
    [InlineData('2', ApprovalPrompt.Deny)]
    [InlineData('3', ApprovalPrompt.AllowDirectory)]
    public void TrySelectDigit_PicksTheMatchingChoice(char digit, string expected)
    {
        var prompt = Create();

        Assert.True(prompt.TrySelectDigit(digit));
        Assert.Equal(expected, prompt.Current.Decision);
    }

    [Theory]
    [InlineData('0')]
    [InlineData('4')]
    [InlineData('x')]
    public void TrySelectDigit_IgnoresAnythingElse(char digit)
    {
        var prompt = Create();
        var before = prompt.Selected;

        Assert.False(prompt.TrySelectDigit(digit));
        Assert.Equal(before, prompt.Selected);
    }

    [Fact]
    public async Task Resolve_CompletesTheDecisionOnce()
    {
        var prompt = Create();

        prompt.Resolve(ApprovalPrompt.AllowOnce);

        Assert.True(prompt.Decision.Task.IsCompletedSuccessfully);
        Assert.Equal(ApprovalPrompt.AllowOnce, await prompt.Decision.Task);
        Assert.False(prompt.Decision.TrySetResult(ApprovalPrompt.Deny));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    public void Render_ProducesAWideFrameOnEveryWidth(int width)
    {
        var prompt = Create();

        var lines = prompt.Render(width);

        Assert.Equal(5, lines.Count);
        Assert.All(lines, line => Assert.Equal(width, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void Render_ListsEveryChoiceUnderTheApprovalTitle()
    {
        var prompt = Create();

        var text = string.Join("\n", prompt.Render(80).Select(TuiText.Strip));

        Assert.Contains("approval · external_directory_required", text, StringComparison.Ordinal);
        Assert.Contains("1 · once", text, StringComparison.Ordinal);
        Assert.Contains("2 · deny", text, StringComparison.Ordinal);
        Assert.Contains("3 · folder", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_MarksTheSelectedChoice()
    {
        var prompt = Create();

        var rows = prompt.Render(80).Select(TuiText.Strip).ToList();
        var denyRow = rows.First(l => l.Contains("2 · deny", StringComparison.Ordinal));
        var allowRow = rows.First(l => l.Contains("1 · once", StringComparison.Ordinal));

        Assert.Contains("❯", denyRow, StringComparison.Ordinal);
        Assert.DoesNotContain("❯", allowRow, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_TruncatesALongSummaryCode()
    {
        var prompt = new ApprovalPrompt(new string('x', 200), [new("1 · once", "d", ApprovalPrompt.AllowOnce)]);

        Assert.All(prompt.Render(60), line => Assert.Equal(60, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void Constructor_RejectsAnEmptyChoiceList()
    {
        Assert.Throws<ArgumentException>(() => new ApprovalPrompt("summary", []));
    }
}

public sealed class TypeAheadSessionTests
{
    private static TypeAheadSession Create() => new(new PendingPromptSlot());

    [Fact]
    public void State_IsBusyUntilSomethingIsQueued()
    {
        var session = Create();

        Assert.Equal(ComposerState.Busy, session.State);
        Assert.Contains("enter queues", session.Hint, StringComparison.Ordinal);
        Assert.Null(session.TopLabel);
    }

    [Fact]
    public void State_BecomesQueuedWithTheQueuedPromptAsLabel()
    {
        var session = Create();
        session.Queue.TryQueue("fix the parser");

        Assert.Equal(ComposerState.Queued, session.State);
        Assert.Contains("fix the parser", session.TopLabel, StringComparison.Ordinal);
        Assert.Contains("sent when this run ends", session.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void TopLabel_FlattensNewlinesAndStaysShort()
    {
        var session = Create();
        session.Queue.TryQueue("first\nsecond" + new string('x', 200));

        var label = session.TopLabel;

        Assert.NotNull(label);
        Assert.DoesNotContain("\n", label, StringComparison.Ordinal);
        Assert.True(label.Length < 90, $"label too long: {label.Length}");
    }

    [Fact]
    public void State_BecomesApprovalWhileAChoiceIsOpen()
    {
        var session = Create();
        session.Queue.TryQueue("queued");

        session.Approval = ApprovalPrompt.For("terminal_delete_requires_approval");

        Assert.True(session.IsApprovalActive);
        Assert.Equal(ComposerState.Approval, session.State);
        Assert.Contains("enter confirm", session.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void FooterHeight_GrowsByTheApprovalBlock()
    {
        var session = Create();
        var withoutApproval = session.FooterHeight(80);

        session.Approval = ApprovalPrompt.For("external_directory_required");

        Assert.Equal(withoutApproval + session.Approval.Render(80).Count, session.FooterHeight(80));
    }

    [Fact]
    public void Hint_ReportsARejectedQueue()
    {
        var session = Create();
        session.Queue.TryQueue("first");
        session.QueueRejected = true;

        Assert.Contains("queue full", session.Hint, StringComparison.Ordinal);
    }
}

public sealed class TypeAheadKeyRoutingTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0', ConsoleModifiers modifiers = default) =>
        new(ch, key, shift: false, alt: false, control: modifiers.HasFlag(ConsoleModifiers.Control));

    private static ConsoleKeyInfo Typed(char ch) => new(ch, ConsoleKey.A, false, false, false);

    [Fact]
    public void Enter_QueuesTheTypedPrompt()
    {
        var session = new TypeAheadSession(new PendingPromptSlot());
        foreach (var ch in "hello")
        {
            InteractiveLineReader.HandleTypeAheadKey(session, Typed(ch));
        }

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Enter, '\r'));

        Assert.Equal("hello", session.Queue.Value);
        Assert.Equal(string.Empty, session.Buffer);
        Assert.False(session.QueueRejected);
    }

    [Fact]
    public void Enter_OnAnEmptyBuffer_QueuesNothing()
    {
        var session = new TypeAheadSession(new PendingPromptSlot());

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Enter, '\r'));

        Assert.False(session.Queue.HasValue);
    }

    [Fact]
    public void Enter_WhenTheSlotIsFull_KeepsTheDraftAndFlagsIt()
    {
        var session = new TypeAheadSession(new PendingPromptSlot());
        session.Queue.TryQueue("already queued");
        foreach (var ch in "second")
        {
            InteractiveLineReader.HandleTypeAheadKey(session, Typed(ch));
        }

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Enter, '\r'));

        Assert.Equal("already queued", session.Queue.Value);
        Assert.Equal("second", session.Buffer);
        Assert.True(session.QueueRejected);
    }

    [Fact]
    public void Backspace_EditsTheDraft()
    {
        var session = new TypeAheadSession(new PendingPromptSlot());
        foreach (var ch in "abc")
        {
            InteractiveLineReader.HandleTypeAheadKey(session, Typed(ch));
        }

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Backspace));

        Assert.Equal("ab", session.Buffer);
    }

    [Fact]
    public void FirstEscape_ClearsTheDraft_SecondCancelsTheTurn()
    {
        var session = new TypeAheadSession(new PendingPromptSlot());
        var cancelled = 0;
        InteractiveLineReader.HandleTypeAheadKey(session, Typed('x'));

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Escape), () => cancelled++);
        Assert.Equal(string.Empty, session.Buffer);
        Assert.Equal(0, cancelled);

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Escape), () => cancelled++);
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void CtrlC_CancelsTheTurnAndCtrlU_ClearsTheDraft()
    {
        var session = new TypeAheadSession(new PendingPromptSlot());
        var cancelled = 0;
        InteractiveLineReader.HandleTypeAheadKey(session, Typed('x'));

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.U, '\u0015', ConsoleModifiers.Control));
        Assert.Equal(string.Empty, session.Buffer);
        Assert.Equal(0, cancelled);

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.C, '\u0003', ConsoleModifiers.Control), () => cancelled++);
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void CtrlO_ExpandsInsteadOfQueuing()
    {
        var session = new TypeAheadSession(new PendingPromptSlot());
        var expanded = 0;

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.O, '\u000f', ConsoleModifiers.Control), onExpand: () => expanded++);

        Assert.Equal(1, expanded);
        Assert.False(session.Queue.HasValue);
    }

    [Fact]
    public void ApprovalOwnsTheKeyboardUntilItIsAnswered()
    {
        var session = new TypeAheadSession(new PendingPromptSlot());
        var prompt = ApprovalPrompt.For("external_directory_required");
        session.Approval = prompt;

        InteractiveLineReader.HandleTypeAheadKey(session, Typed('x'));
        Assert.Equal(string.Empty, session.Buffer);

        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.UpArrow));
        Assert.Equal(ApprovalPrompt.AllowOnce, prompt.Current.Decision);

        InteractiveLineReader.HandleTypeAheadKey(session, Typed('3'));
        Assert.Equal(ApprovalPrompt.AllowDirectory, prompt.Current.Decision);
    }

    [Fact]
    public async Task ApprovalConfirmAndEscapeResolveTheDecision()
    {
        var session = new TypeAheadSession(new PendingPromptSlot());
        var confirm = ApprovalPrompt.For("external_directory_required");
        session.Approval = confirm;
        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Enter, '\r'));
        Assert.Equal(ApprovalPrompt.Deny, await confirm.Decision.Task);

        var escape = ApprovalPrompt.For("external_directory_required");
        session.Approval = escape;
        InteractiveLineReader.HandleTypeAheadKey(session, Key(ConsoleKey.Escape));
        Assert.Equal(ApprovalPrompt.Deny, await escape.Decision.Task);
    }
}
