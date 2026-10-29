using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Controls;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

/// <summary>An activity row's headline: verb, subject, numbers and exit code.</summary>
public sealed class ActivityRowHeadlineTests
{
    [Theory]
    [InlineData(AgentActivityKind.Terminal, "Terminal", AgentActivityState.Started, "Running")]
    [InlineData(AgentActivityKind.Terminal, "Terminal", AgentActivityState.Completed, "Ran")]
    [InlineData(AgentActivityKind.Terminal, "Output", AgentActivityState.Completed, "Read output")]
    [InlineData(AgentActivityKind.Terminal, "Stop", AgentActivityState.Completed, "Stopped")]
    [InlineData(AgentActivityKind.Explore, "List", AgentActivityState.Completed, "Listed")]
    [InlineData(AgentActivityKind.Explore, "Glob", AgentActivityState.Started, "Finding files")]
    [InlineData(AgentActivityKind.Analyze, "View", AgentActivityState.Completed, "Read")]
    [InlineData(AgentActivityKind.Analyze, "Docs", AgentActivityState.Completed, "Read the docs")]
    [InlineData(AgentActivityKind.Edit, "Write", AgentActivityState.Completed, "Wrote")]
    [InlineData(AgentActivityKind.Web, "Fetch", AgentActivityState.Started, "Fetching")]
    [InlineData(AgentActivityKind.Web, "Web search", AgentActivityState.Completed, "Searched the web")]
    [InlineData(AgentActivityKind.Mcp, "MCP", AgentActivityState.Completed, "Called")]
    [InlineData(AgentActivityKind.Computer, "Computer", AgentActivityState.Started, "Using the computer")]
    [InlineData(AgentActivityKind.Program, "Program", AgentActivityState.Completed, "Ran a program")]
    public void The_verb_follows_the_kind_tool_and_tense(AgentActivityKind kind, string tool, AgentActivityState state, string verb)
    {
        Assert.Equal(verb, Item(kind, state, toolDisplay: tool).RowVerb);
    }

    [Fact]
    public void Subjects_name_what_the_row_acted_on()
    {
        Assert.Equal("dotnet build -c Release", Item(AgentActivityKind.Terminal, AgentActivityState.Started, detail: "dotnet build -c Release\nsecond line").RowSubject);
        Assert.Equal("\"PageCursor\"", Item(AgentActivityKind.Search, AgentActivityState.Completed, symbol: "PageCursor").RowSubject);
        Assert.Equal("history_paging.py", Item(AgentActivityKind.Analyze, AgentActivityState.Completed, fileName: "history_paging.py").RowSubject);
        Assert.Equal("github › list_issues", Item(AgentActivityKind.Mcp, AgentActivityState.Completed, detail: "github › list_issues").RowSubject);
        Assert.Equal("invoice-check", Item(AgentActivityKind.Skill, AgentActivityState.Completed, detail: "invoice-check").RowSubject);
        var search = Item(AgentActivityKind.Skill, AgentActivityState.Completed, detail: "search: pdf");
        Assert.Equal("\"pdf\"", search.RowSubject);
        Assert.Equal("Searched skills", search.RowVerb);
    }

    [Fact]
    public void Numbers_follow_the_subject_in_the_tail()
    {
        Assert.Equal("L40–120", Item(AgentActivityKind.Analyze, AgentActivityState.Completed, fileName: "a.py", lineStart: 40, lineEnd: 120).RowMeta);
        Assert.Equal("12 results", Item(AgentActivityKind.Search, AgentActivityState.Completed, symbol: "x", count: 12).RowMeta);
        Assert.Equal("2.1s", Item(AgentActivityKind.Terminal, AgentActivityState.Completed, detail: "pytest", durationMs: 2100).RowMeta);
        Assert.Equal("4s", Item(AgentActivityKind.Thinking, AgentActivityState.Completed, durationMs: 4000).RowMeta);
        Assert.Equal(string.Empty, Item(AgentActivityKind.Thinking, AgentActivityState.Started, durationMs: 4000).RowMeta);
    }

    [Fact]
    public void A_failing_exit_code_is_shown_and_kept_when_a_later_frame_omits_it()
    {
        var item = Item(AgentActivityKind.Terminal, AgentActivityState.Completed, detail: "pytest", exitCode: 1);
        Assert.True(item.HasFailingExitCode);
        Assert.Equal("exit 1", item.ExitCodeText);

        item.UpdateFrom(Model(AgentActivityKind.Terminal, AgentActivityState.Completed, detail: "pytest"));
        Assert.Equal(1, item.ExitCode);

        Assert.False(Item(AgentActivityKind.Terminal, AgentActivityState.Completed, exitCode: 0).HasFailingExitCode);
        Assert.False(Item(AgentActivityKind.Analyze, AgentActivityState.Completed, exitCode: 3).HasFailingExitCode);
    }

    [Fact]
    public void Replayed_history_keeps_the_exit_code()
    {
        // The GUI parses the activity replay itself; dropping the field there left every
        // reopened session without its exit badges while live rows had them.
        Assert.Contains("wire.ExitCode", GuiSource.Text("Services", "ApiService.AgentHistory.cs"));
    }

    [Fact]
    public void The_verb_moves_with_the_state()
    {
        var item = Item(AgentActivityKind.Terminal, AgentActivityState.Started, toolDisplay: "Terminal", detail: "pytest");
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        item.UpdateFrom(Model(AgentActivityKind.Terminal, AgentActivityState.Completed, toolDisplay: "Terminal", detail: "pytest"));
        Assert.Equal("Ran", item.RowVerb);
        Assert.Contains(nameof(AgentActivityItem.RowVerb), changed);
    }

    [Fact]
    public void Every_kind_has_a_glyph_and_new_kinds_are_distinct()
    {
        foreach (var kind in Enum.GetValues<AgentActivityKind>())
            Assert.False(string.IsNullOrEmpty(ActivityRowGlyphs.For(kind, null)));
        var tools = new[] { AgentActivityKind.Web, AgentActivityKind.Mcp, AgentActivityKind.Computer, AgentActivityKind.Skill, AgentActivityKind.Program };
        Assert.Equal(tools.Length, tools.Select(kind => ActivityRowGlyphs.For(kind, null)).Distinct().Count());
    }

    private static AgentActivityItem Item(AgentActivityKind kind, AgentActivityState state, string? toolDisplay = null,
        string? fileName = null, int? lineStart = null, int? lineEnd = null, string? symbol = null, int? count = null,
        int? durationMs = null, string? detail = null, int? exitCode = null) =>
        new(Model(kind, state, toolDisplay, fileName, lineStart, lineEnd, symbol, count, durationMs, detail, exitCode));

    private static AgentActivityModel Model(AgentActivityKind kind, AgentActivityState state, string? toolDisplay = null,
        string? fileName = null, int? lineStart = null, int? lineEnd = null, string? symbol = null, int? count = null,
        int? durationMs = null, string? detail = null, int? exitCode = null) =>
        new("act_22222222222222222222222222222222", kind, state, fileName, lineStart, lineEnd, symbol, count,
            null, null, durationMs, "succeeded", toolDisplay, null, null, detail, null, exitCode);
}
