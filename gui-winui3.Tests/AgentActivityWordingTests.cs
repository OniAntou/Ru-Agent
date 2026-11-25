using System.Collections.ObjectModel;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Rows name what each tool did, and the process header follows the run as each step
/// starts and settles, instead of a fixed "Working…" and generic status rows.
/// </summary>
public sealed class AgentActivityWordingTests
{
    [Fact]
    public void The_live_header_follows_a_row_that_settles_in_place()
    {
        var command = Row("cmd", AgentActivityKind.Terminal, AgentActivityState.Started, detail: "dotnet test");
        var message = new ChatMessageItem
        {
            Role = "assistant", IsStreaming = true, CompactAgentActivity = true,
            AgentActivities = new ObservableCollection<AgentActivityItem> { command },
        };
        Assert.Equal("Running dotnet test…", message.ProcessHeader);
        var raised = new List<string?>();
        message.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        // The collection does not change when a row settles; the header still has to.
        command.UpdateFrom(Model("cmd", AgentActivityKind.Terminal, AgentActivityState.Completed, detail: "dotnet test"));

        Assert.Contains(nameof(ChatMessageItem.ProcessHeader), raised);
        Assert.Equal("Working…", message.ProcessHeader);
        Assert.Equal("1 step", message.ProcessMeta);

        message.IsStreaming = false;
        Assert.Equal("Ran 1 command · 1 step", message.ProcessMeta);
    }

    [Fact]
    public void A_status_row_named_after_its_tool_says_what_it_did()
    {
        var sent = Row("send", AgentActivityKind.Status, AgentActivityState.Completed, tool: "Message subagent");
        Assert.Equal("Messaged a subagent", sent.DisplayText);
        Assert.True(sent.IsVisibleInFeed);
        Assert.True(sent.CountsAsStep);

        var asking = Row("ask", AgentActivityKind.Status, AgentActivityState.Waiting, tool: "Question", code: "input_required");
        Assert.Equal("Waiting for your answer", asking.RowVerb);
        var asked = Row("ask", AgentActivityKind.Status, AgentActivityState.Completed, tool: "Question");
        Assert.Equal("Asked a question", asked.RowVerb);

        // A status that is only the word for success still earns no row and no step.
        var silent = Row("ok", AgentActivityKind.Status, AgentActivityState.Completed);
        Assert.False(silent.IsVisibleInFeed);
        Assert.False(silent.CountsAsStep);
    }

    [Theory]
    [InlineData("open github.com", AgentActivityState.Completed, "Opened", "github.com")]
    [InlineData("click e12", AgentActivityState.Started, "Clicking", "e12")]
    [InlineData("type 24 characters", AgentActivityState.Completed, "Typed", "24 characters")]
    [InlineData("screenshot", AgentActivityState.Completed, "Took a screenshot", "")]
    [InlineData("eval", AgentActivityState.Completed, "Ran a page script", "")]
    public void Browser_rows_name_the_action_and_its_target(string detail, AgentActivityState state, string verb, string subject)
    {
        var row = Row("b", AgentActivityKind.Web, state, tool: "Browser", detail: detail);
        Assert.Equal(verb, row.RowVerb);
        Assert.Equal(subject, row.RowSubject);
    }

    [Fact]
    public void Empty_results_say_so_instead_of_counting_zero()
    {
        Assert.Equal("Found no files", Row("g", AgentActivityKind.Explore, AgentActivityState.Completed, tool: "Glob", count: 0).RowVerb);
        Assert.Equal("Listed an empty folder", Row("l", AgentActivityKind.Explore, AgentActivityState.Completed, tool: "List", count: 0).RowVerb);
        Assert.Equal("Explored no items", Row("e", AgentActivityKind.Explore, AgentActivityState.Completed, count: 0).DisplayText);
        var search = Row("s", AgentActivityKind.Search, AgentActivityState.Completed, count: 0, symbol: "steer", detail: "src");
        Assert.Equal("no results", search.RowMeta);
        Assert.Equal("\"steer\" in src", search.RowSubject);
        Assert.EndsWith("· no matches", search.DisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_glob_row_names_its_pattern_and_how_many_files_matched()
    {
        var found = Row("g", AgentActivityKind.Explore, AgentActivityState.Completed, tool: "Glob", count: 12, detail: "**/*.cs");
        Assert.Equal("Found files", found.RowVerb);
        Assert.Equal("**/*.cs", found.RowSubject);
        Assert.Equal("12 files", found.RowMeta);
        Assert.Equal("Searched for files", RuApp.Services.AgentActivitySummaryProjection.ForSteps([found], live: false).Recap);
    }

    [Fact]
    public void The_live_header_shows_the_running_thinking_line()
    {
        // The folded turn names the step ("Thinking…"); once the model streams a line it
        // follows the same absolute line the row shows, and drops it when the turn settles.
        var thinking = Row("think", AgentActivityKind.Thinking, AgentActivityState.Started);
        var message = new ChatMessageItem
        {
            Role = "assistant", IsStreaming = true, CompactAgentActivity = true,
            AgentActivities = new ObservableCollection<AgentActivityItem> { thinking },
        };
        Assert.Equal("Thinking…", message.ProcessHeader);
        Assert.Equal(string.Empty, message.ProcessMeta);

        var raised = new List<string?>();
        message.PropertyChanged += (_, args) => raised.Add(args.PropertyName);
        thinking.SetThinkingPreview("Inspecting the session store");

        Assert.Contains(nameof(ChatMessageItem.ProcessMeta), raised);
        Assert.Equal("Thinking", message.ProcessHeader);
        Assert.Equal("Inspecting the session store", message.ProcessMeta);

        message.IsStreaming = false;
        Assert.DoesNotContain("Inspecting", message.ProcessMeta, StringComparison.Ordinal);
    }

    private static AgentActivityItem Row(string id, AgentActivityKind kind, AgentActivityState state, string? tool = null,
        string? detail = null, int? count = null, string? symbol = null, string code = "succeeded") =>
        new(Model(id, kind, state, tool, detail, count, symbol, code));

    private static AgentActivityModel Model(string id, AgentActivityKind kind, AgentActivityState state, string? tool = null,
        string? detail = null, int? count = null, string? symbol = null, string code = "succeeded") =>
        new(id, kind, state, null, null, null, symbol, count, null, null, null, code, tool, null, null, detail);
}
