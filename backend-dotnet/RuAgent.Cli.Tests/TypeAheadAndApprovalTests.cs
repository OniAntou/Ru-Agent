using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ApprovalPromptTests
{
    private static ApprovalPrompt Create(bool offerDirectory = false) =>
        ApprovalPrompt.For("external_directory_required", offerDirectory);

    [Fact]
    public void For_OffersTheThreeWayChoiceWithDenySelected()
    {
        var prompt = Create(offerDirectory: true);

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
    public void For_KnownNonExternalAsk_OffersAlwaysChoice()
    {
        var prompt = ApprovalPrompt.For("terminal_delete_requires_approval");

        Assert.Equal(3, prompt.Choices.Count);
        Assert.Equal(ApprovalPrompt.AllowOnce, prompt.Choices[0].Decision);
        Assert.Equal(ApprovalPrompt.Deny, prompt.Choices[1].Decision);
        Assert.Equal(ApprovalPrompt.AllowAlways, prompt.Choices[2].Decision);
    }

    [Fact]
    public void For_ExternalAsk_NeverOffersAlwaysChoice()
    {
        var prompt = ApprovalPrompt.For("external_directory_required", offerDirectory: true);

        Assert.DoesNotContain(prompt.Choices, c => c.Decision == ApprovalPrompt.AllowAlways);
    }

    [Fact]
    public void For_UnknownAsk_OffersNeitherFolderNorAlways()
    {
        var prompt = ApprovalPrompt.For("future_reason_code", offerDirectory: true);

        Assert.Equal(2, prompt.Choices.Count);
    }

    [Fact]
    public void ForWorkspaceTrust_DefaultsToCancelAndOffersNoEscalation()
    {
        var prompt = ApprovalPrompt.ForWorkspaceTrust(@"C:\work\repo");

        Assert.Equal(2, prompt.Choices.Count);
        Assert.Equal(ApprovalPrompt.TrustApprove, prompt.Choices[0].Decision);
        Assert.Equal(ApprovalPrompt.Deny, prompt.Choices[1].Decision);

        // A bare Enter must never widen what the agent may read, so the safe answer is
        // the one the prompt starts on.
        Assert.Equal(ApprovalPrompt.Deny, prompt.Current.Decision);
        Assert.DoesNotContain(prompt.Choices, c => c.Decision == ApprovalPrompt.AllowDirectory);
    }

    [Fact]
    public void ForWorkspaceTrust_NamesTheWorkspaceUnderATrustTitle()
    {
        var prompt = ApprovalPrompt.ForWorkspaceTrust(@"C:\work\repo");
        var frame = string.Join("\n", prompt.Render(80).Select(TuiText.Strip));

        Assert.Contains("trust", frame, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\work\repo", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void ForWorkspaceTrust_EscapeDenies()
    {
        var prompt = ApprovalPrompt.ForWorkspaceTrust(@"C:\work\repo");

        Assert.Equal(
            ChoiceOutcome.Cancelled,
            prompt.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false)));
    }

    [Fact]
    public void Move_WalksAndWrapsTheChoices()
    {
        var prompt = Create(offerDirectory: true);
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
        var prompt = Create(offerDirectory: true);

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
        var prompt = Create(offerDirectory: true);

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
        var prompt = Create(offerDirectory: true);

        var lines = prompt.Render(width);

        Assert.Equal(5, lines.Count);
        Assert.All(lines, line => Assert.Equal(width, TuiText.VisibleWidth(line)));
    }

    [Fact]
    public void Render_ListsEveryChoiceUnderTheApprovalTitle()
    {
        var prompt = Create(offerDirectory: true);

        var text = string.Join("\n", prompt.Render(80).Select(TuiText.Strip));

        Assert.Contains("approval · external_directory_required", text, StringComparison.Ordinal);
        Assert.Contains("1 · once", text, StringComparison.Ordinal);
        Assert.Contains("2 · deny", text, StringComparison.Ordinal);
        Assert.Contains("3 · folder", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_KeepsTheFooterExactlyWithoutTheRowCount()
    {
        var prompt = Create(offerDirectory: true);

        var text = string.Join("\n", prompt.Render(80).Select(TuiText.Strip));

        Assert.Contains("↑↓ · 1-9 · enter · esc", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rows", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_MarksTheSelectedChoice()
    {
        var prompt = Create(offerDirectory: true);

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
    public void For_OnlyExternalDirectoryAsksOfferTheFolderChoice()
    {
        Assert.Equal(3, ApprovalPrompt.For("terminal_delete_requires_approval", offerDirectory: true).Choices.Count);
        Assert.Equal(3, ApprovalPrompt.For("web_approval_required", offerDirectory: true).Choices.Count);
        Assert.Equal(2, ApprovalPrompt.For("approval_required").Choices.Count);
        Assert.Equal(3, ApprovalPrompt.For("external_directory_required", offerDirectory: true).Choices.Count);
    }

    [Fact]
    public void Constructor_RejectsAnEmptyChoiceList()
    {
        Assert.Throws<ArgumentException>(() => new ApprovalPrompt("summary", []));
    }
}
