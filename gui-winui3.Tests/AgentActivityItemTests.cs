using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentActivityItemTests
{
    [Fact]
    public void Item_renders_safe_file_metadata_and_updates_state_text()
    {
        var started = Activity(
            AgentActivityState.Started,
            fileName: "ChatView.xaml.cs",
            lineStart: 150,
            lineEnd: 200);

        var item = new AgentActivityItem(started);

        Assert.True(item.IsRunning);
        Assert.Equal("Đang phân tích", item.DisplayText);
        Assert.Equal("ChatView.xaml.cs · L150–200", item.MetadataText);

        item.UpdateFrom(Activity(
            AgentActivityState.Completed,
            fileName: "ChatView.xaml.cs",
            lineStart: 150,
            lineEnd: 200,
            durationMs: 1200));

        Assert.True(item.IsCompleted);
        Assert.Equal("Đã phân tích", item.DisplayText);
        Assert.Equal("\uE73E", item.StatusGlyph);
    }

    [Fact]
    public void Item_keeps_terminal_text_bounded_to_safe_code()
    {
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Failed,
            safeCode: "policy_denied"));

        Assert.True(item.IsFailed);
        Assert.Equal("Không thể hoàn tất · policy_denied", item.DisplayText);
    }

    private static AgentActivityModel Activity(
        AgentActivityState state,
        string? fileName = null,
        int? lineStart = null,
        int? lineEnd = null,
        int? durationMs = null,
        string safeCode = "succeeded") =>
        new(
            "act_11111111111111111111111111111111",
            AgentActivityKind.Analyze,
            state,
            fileName,
            lineStart,
            lineEnd,
            null,
            null,
            null,
            null,
            durationMs,
            safeCode,
            null,
            1);
}
