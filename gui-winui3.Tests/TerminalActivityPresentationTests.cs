using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class TerminalActivityPresentationTests
{
    [Fact]
    public void A_command_is_inspectable_before_it_prints_any_output()
    {
        var item = new AgentActivityItem(Activity(AgentActivityState.Started, "Write-Output 'hello'"));
        Assert.True(item.ShowRowDetailToggle);
        Assert.False(item.ShowPlainHeader);
        item.IsEditPreviewExpanded = true;
        Assert.True(item.ShowTerminalOutput);
        Assert.Contains("Write-Output 'hello'", item.RowSubject);
        Assert.Contains("Write-Output 'hello'", item.TerminalPanelText);
    }

    [Fact]
    public void Completion_without_detail_preserves_the_multiline_command_beside_its_output()
    {
        const string command = "$msg = @'\nhello `world`\n'@\nWrite-Output $msg";
        var item = new AgentActivityItem(Activity(AgentActivityState.Started, command));
        item.AppendTerminalOutput("hello `world`\n");
        item.IsEditPreviewExpanded = true;
        item.UpdateFrom(Activity(AgentActivityState.Completed, null) with { ExitCode = 0 });
        Assert.Contains(command, item.TerminalPanelText);
        Assert.EndsWith("hello `world`\n", item.TerminalPanelText);
        Assert.True(item.ShowTerminalOutput);
        Assert.Contains("$msg", item.RowSubject);
    }

    [Fact]
    public void A_retry_clears_superseded_output_but_keeps_the_command_open()
    {
        var item = new AgentActivityItem(Activity(AgentActivityState.Started, "dotnet build"));
        item.IsEditPreviewExpanded = true;
        item.AppendTerminalOutput("old attempt");
        item.ResetTerminalOutput();
        Assert.True(item.IsEditPreviewExpanded);
        Assert.True(item.ShowTerminalOutput);
        Assert.False(item.HasTerminalOutput);
        Assert.DoesNotContain("old attempt", item.TerminalPanelText);
        Assert.Contains("dotnet build", item.TerminalPanelText);
    }

    [Fact]
    public void Output_is_bounded_and_the_card_reports_truncation_until_reset()
    {
        var item = new AgentActivityItem(Activity(AgentActivityState.Started, "generate-output"));
        item.AppendTerminalOutput(new string('x', TerminalOutputBlock.MaximumCharacters + 100));
        Assert.Equal(TerminalOutputBlock.MaximumCharacters, item.TerminalOutputText.Length);
        Assert.True(item.TerminalOutputTruncated);
        item.ResetTerminalOutput();
        Assert.False(item.TerminalOutputTruncated);
        Assert.False(item.HasTerminalOutput);
    }

    [Fact]
    public void A_finished_command_without_captured_output_does_not_claim_it_printed_nothing()
    {
        var item = new AgentActivityItem(Activity(AgentActivityState.Completed, "git diff --quiet") with { ExitCode = 0 });
        Assert.Equal("exit code 0", item.TerminalStatusText);
        Assert.Equal("No streamed output captured.", item.TerminalOutputDisplayText);
        Assert.False(item.TerminalHasFailed);
        Assert.True(item.ShowRowDetailToggle);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Failed_exit_codes_remain_visible_including_signed_codes(int code)
    {
        var item = new AgentActivityItem(Activity(AgentActivityState.Completed, "command") with { ExitCode = code });
        Assert.Equal($"exit code {code}", item.TerminalStatusText);
        Assert.True(item.TerminalHasFailed);
    }

    private static AgentActivityModel Activity(AgentActivityState state, string? command) =>
        new("act_terminal_1", AgentActivityKind.Terminal, state, null, null, null, null, null,
            null, null, null, "succeeded", "Pwsh", null, 1, command);
}
