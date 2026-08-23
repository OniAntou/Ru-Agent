using System.Text.Json;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentApprovalTests
{
    [Fact]
    public void Approval_expires_and_exposes_only_safe_action()
    {
        var approval = new AgentApprovalModel(
            "approval-1",
            "session-1",
            "terminal.shell",
            "Run the approved diagnostic.",
            "terminal.execute",
            DateTimeOffset.UtcNow.AddSeconds(30));

        Assert.Equal("approval-1", approval.ApprovalId);
        Assert.Equal("terminal.shell", approval.ToolId);
        Assert.False(approval.IsExpired());
        Assert.True(approval.IsExpired(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.DoesNotContain("token", approval.SafeAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_accepts_a_frame_without_preview_and_leaves_it_null()
    {
        using var document = ParseValidFrame();

        Assert.True(AgentApprovalModel.TryParse(document.RootElement, out var approval));
        Assert.Equal("approval-1", approval.ApprovalId);
        Assert.Equal("session-1", approval.SessionId);
        Assert.Equal("terminal.shell", approval.ToolId);
        Assert.Null(approval.Preview);
    }

    [Fact]
    public void TryParse_reads_the_optional_preview_verbatim()
    {
        string json = """
            {"type":"agent.approval_required","approval_id":"approval-1","session_id":"session-1","tool_id":"files.write","content":"Ghi file a.txt","scope":"workspace.write","expires_at":"2099-01-01T00:00:00Z","preview":"new content"}
            """;
        using var document = JsonDocument.Parse(json);

        Assert.True(AgentApprovalModel.TryParse(document.RootElement, out var approval));
        Assert.Equal("new content", approval.Preview);
    }

    [Fact]
    public void TryParse_truncates_an_oversized_preview_to_512_characters()
    {
        string oversized = new('x', 600);
        string json = BuildFrameJson(preview: oversized);
        using var document = JsonDocument.Parse(json);

        Assert.True(AgentApprovalModel.TryParse(document.RootElement, out var approval));
        Assert.NotNull(approval.Preview);
        Assert.Equal(512, approval.Preview!.Length);
        Assert.Equal(new string('x', 512), approval.Preview);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", "   ")]
    public void TryParse_normalizes_an_empty_preview_to_null(string preview, string? expected)
    {
        string json = BuildFrameJson(preview: preview);
        using var document = JsonDocument.Parse(json);

        Assert.True(AgentApprovalModel.TryParse(document.RootElement, out var approval));
        Assert.Equal(expected, approval.Preview);
    }

    [Fact]
    public void TryParse_still_rejects_frames_missing_required_fields()
    {
        string json = """
            {"type":"agent.approval_required","approval_id":"","session_id":"session-1","tool_id":"files.write","content":"Ghi file","scope":"workspace.write","expires_at":"2099-01-01T00:00:00Z"}
            """;
        using var document = JsonDocument.Parse(json);

        Assert.False(AgentApprovalModel.TryParse(document.RootElement, out _));
    }

    private static JsonDocument ParseValidFrame()
    {
        return JsonDocument.Parse(BuildFrameJson());
    }

    private static string BuildFrameJson(string? preview = null)
    {
        var frame = new
        {
            type = "agent.approval_required",
            approval_id = "approval-1",
            session_id = "session-1",
            tool_id = "terminal.shell",
            content = "Run the approved diagnostic.",
            scope = "terminal.execute",
            expires_at = "2099-01-01T00:00:00Z",
            preview,
        };
        return JsonSerializer.Serialize(frame);
    }
}
