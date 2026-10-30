using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class ModernCodingGuidanceTests
{
    [Fact]
    public void Current_file_tools_receive_creation_editing_and_line_read_guidance()
    {
        var guidance = string.Join("\n", AgentToolPromptGuidance.ForTools(["read_file", "write_file", "edit_file"]));
        Assert.Contains("read_file", guidance);
        Assert.Contains("write_file", guidance);
        Assert.Contains("edit_file", guidance);
    }

    [Theory]
    [InlineData("run_process")]
    [InlineData("powershell")]
    public void Each_current_terminal_launch_tool_receives_verification_intent_guidance(string tool)
    {
        var guidance = string.Join("\n", AgentToolPromptGuidance.ForTools([tool]));
        Assert.Contains("purpose=verification", guidance);
        Assert.Contains("purpose=inspection", guidance);
    }

    [Theory]
    [InlineData("run_process")]
    [InlineData("powershell")]
    public void Terminal_verification_guidance_keeps_failed_checks_outstanding_without_retirement_language(string tool)
    {
        var guidance = string.Join("\n", AgentToolPromptGuidance.ForTools([tool]));
        Assert.Contains("a failed or pending required check stays outstanding until the same check passes", guidance);
        Assert.Contains("Rerun a check after later edits", guidance);
        Assert.DoesNotContain("supersedes", guidance);
        Assert.DoesNotContain("stale", guidance);
    }
}
