using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Approval panel presentation behaviour, exercised through the policy the chat
/// view calls rather than by matching strings in ChatView.xaml.cs.
/// </summary>
public sealed class AgentApprovalPresentationTests
{
    [Fact]
    public void The_controller_proposal_wins_over_the_activity_feed()
    {
        var command = AgentApprovalPresentation.ResolveTerminalCommand(
            "git push origin main",
            [Terminal("rm -rf build/")]);

        Assert.Equal("git push origin main", command);
    }

    [Fact]
    public void A_blank_proposal_falls_back_to_the_newest_terminal_activity()
    {
        // Reattaching mid-run leaves the controller without the proposal, so the
        // activity feed is the only source. The newest terminal row wins.
        var command = AgentApprovalPresentation.ResolveTerminalCommand(
            "   ",
            [Terminal("first"), Terminal("second")]);

        Assert.Equal("second", command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_proposal_and_no_terminal_activity_yields_nothing(string? proposal)
    {
        var command = AgentApprovalPresentation.ResolveTerminalCommand(proposal, []);

        Assert.Null(command);
    }

    [Fact]
    public void Non_terminal_activity_never_becomes_the_approved_command()
    {
        // Reading a file is not something the user approves as a command.
        var read = Activity(AgentActivityKind.Analyze, "ChatView.xaml.cs");
        var terminalWithoutDetail = Activity(AgentActivityKind.Terminal, null);

        Assert.Null(AgentApprovalPresentation.ResolveTerminalCommand(null, [read, terminalWithoutDetail]));
    }

    [Fact]
    public void A_terminal_row_without_detail_is_skipped_for_an_earlier_one_with_detail()
    {
        var command = AgentApprovalPresentation.ResolveTerminalCommand(
            null,
            [Terminal("dotnet test"), Activity(AgentActivityKind.Terminal, null)]);

        Assert.Equal("dotnet test", command);
    }

    private static AgentActivityItem Terminal(string? detail) =>
        Activity(AgentActivityKind.Terminal, detail);

    private static AgentActivityItem Activity(AgentActivityKind kind, string? detail) =>
        new(new AgentActivityModel(
            "act_11111111111111111111111111111111",
            kind,
            AgentActivityState.Waiting,
            FileName: null,
            LineStart: null,
            LineEnd: null,
            Symbol: null,
            Count: null,
            Added: null,
            Removed: null,
            DurationMs: null,
            SafeCode: "waiting",
            ToolDisplay: null,
            DurableSequence: null,
            TransientSequence: 1,
            Detail: detail));
}
