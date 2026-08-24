using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class AgentCliParserTests
{
    private static string ValidRunId => "run_" + new string('a', 32);
    private static string ValidRunId2 => "run_" + new string('b', 32);
    private static string ValidWorkspace => Path.GetTempPath().TrimEnd('\\', '/');

    [Fact]
    public void ParseRun_SucceedsWithRequiredArgs()
    {
        var args = new[] { "agent", "run", "--workspace", ValidWorkspace, "hello prompt" };
        var res = AgentCliParser.Parse(args);
        Assert.True(res.Success);
        var cmd = Assert.IsType<RunCommand>(res.Command);
        Assert.Equal(ValidWorkspace, cmd.WorkspacePath);
        Assert.Equal("hello prompt", cmd.Prompt);
        Assert.Equal("standard", cmd.Preset);
        Assert.False(cmd.Json);
        Assert.Null(cmd.SessionId);
    }

    [Fact]
    public void ParseRun_SucceedsWithOptionalPresetAndJsonAndSession()
    {
        var sessionId = new string('c', 32); // 32 hex lower, valid loose
        var args = new[] { "agent", "run", "--preset", "standard", "--workspace", ValidWorkspace, "--session", sessionId, "--json", "my prompt with spaces" };
        var res = AgentCliParser.Parse(args);
        Assert.True(res.Success);
        var cmd = Assert.IsType<RunCommand>(res.Command);
        Assert.Equal("standard", cmd.Preset);
        Assert.True(cmd.Json);
        Assert.Equal(sessionId, cmd.SessionId);
        Assert.Equal("my prompt with spaces", cmd.Prompt);
    }

    [Fact]
    public void ParseRun_RejectsPresetFullWithPhaseNotAvailable()
    {
        var args = new[] { "agent", "run", "--workspace", ValidWorkspace, "--preset", "full", "prompt" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("phase_not_available", res.ErrorCode);
    }

    [Fact]
    public void ParseRun_RejectsMissingWorkspace()
    {
        var args = new[] { "agent", "run", "prompt without workspace" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("missing_workspace", res.ErrorCode);
    }

    [Fact]
    public void ParseRun_RejectsMissingPrompt()
    {
        var args = new[] { "agent", "run", "--workspace", ValidWorkspace };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("missing_prompt", res.ErrorCode);
    }

    [Fact]
    public void ParseRun_RejectsUnknownSwitch()
    {
        var args = new[] { "agent", "run", "--workspace", ValidWorkspace, "--unknown", "prompt" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("unknown_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseRun_RejectsDuplicateWorkspace()
    {
        var args = new[] { "agent", "run", "--workspace", ValidWorkspace, "--workspace", ValidWorkspace, "prompt" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("duplicate_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseRun_RejectsDuplicatePreset()
    {
        var args = new[] { "agent", "run", "--workspace", ValidWorkspace, "--preset", "standard", "--preset", "standard", "prompt" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("duplicate_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseRun_RejectsDuplicateJson()
    {
        var args = new[] { "agent", "run", "--workspace", ValidWorkspace, "--json", "--json", "prompt" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("duplicate_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseRun_RejectsDuplicateSession()
    {
        var sid = new string('d', 32);
        var args = new[] { "agent", "run", "--workspace", ValidWorkspace, "--session", sid, "--session", sid, "prompt" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("duplicate_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseAttach_Succeeds()
    {
        var args = new[] { "agent", "attach", ValidRunId };
        var res = AgentCliParser.Parse(args);
        Assert.True(res.Success);
        var cmd = Assert.IsType<AttachCommand>(res.Command);
        Assert.Equal(ValidRunId, cmd.RunId);
        Assert.False(cmd.Json);
    }

    [Fact]
    public void ParseAttach_SucceedsWithJson()
    {
        var args = new[] { "agent", "attach", ValidRunId, "--json" };
        var res = AgentCliParser.Parse(args);
        Assert.True(res.Success);
        var cmd = Assert.IsType<AttachCommand>(res.Command);
        Assert.True(cmd.Json);
    }

    [Fact]
    public void ParseAttach_RejectsUnknownSwitch()
    {
        var args = new[] { "agent", "attach", ValidRunId, "--foobar" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("unknown_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseAttach_RejectsDuplicateJson()
    {
        var args = new[] { "agent", "attach", ValidRunId, "--json", "--json" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("duplicate_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseAttach_RejectsMissingRunId()
    {
        var args = new[] { "agent", "attach" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("missing_run_id", res.ErrorCode);
    }

    [Fact]
    public void ParseTrust_SucceedsApprove()
    {
        var args = new[] { "agent", "trust", ValidRunId, "--approve" };
        var res = AgentCliParser.Parse(args);
        Assert.True(res.Success);
        var cmd = Assert.IsType<TrustCommand>(res.Command);
        Assert.True(cmd.Approve);
        Assert.Equal(ValidRunId, cmd.RunId);
    }

    [Fact]
    public void ParseTrust_SucceedsDeny()
    {
        var args = new[] { "agent", "trust", ValidRunId, "--deny" };
        var res = AgentCliParser.Parse(args);
        Assert.True(res.Success);
        var cmd = Assert.IsType<TrustCommand>(res.Command);
        Assert.False(cmd.Approve);
    }

    [Fact]
    public void ParseTrust_RejectsBothApproveAndDeny()
    {
        var args = new[] { "agent", "trust", ValidRunId, "--approve", "--deny" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("duplicate_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseTrust_RejectsDuplicateApprove()
    {
        var args = new[] { "agent", "trust", ValidRunId, "--approve", "--approve" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("duplicate_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseTrust_RejectsDuplicateDeny()
    {
        var args = new[] { "agent", "trust", ValidRunId, "--deny", "--deny" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("duplicate_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseTrust_RejectsMissingDecision()
    {
        var args = new[] { "agent", "trust", ValidRunId };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("missing_decision", res.ErrorCode);
    }

    [Fact]
    public void ParseTrust_RejectsUnknownSwitch()
    {
        var args = new[] { "agent", "trust", ValidRunId, "--approve", "--unknown" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("unknown_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseCancel_Succeeds()
    {
        var args = new[] { "agent", "cancel", ValidRunId };
        var res = AgentCliParser.Parse(args);
        Assert.True(res.Success);
        var cmd = Assert.IsType<CancelCommand>(res.Command);
        Assert.Equal(ValidRunId, cmd.RunId);
    }

    [Fact]
    public void ParseCancel_RejectsUnknownSwitch()
    {
        var args = new[] { "agent", "cancel", ValidRunId, "--json" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("unknown_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseCancel_RejectsMissingRunId()
    {
        var args = new[] { "agent", "cancel" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("missing_run_id", res.ErrorCode);
    }

    [Fact]
    public void ParseContinue_Succeeds()
    {
        var args = new[] { "agent", "continue", ValidRunId };
        var res = AgentCliParser.Parse(args);
        Assert.True(res.Success);
        var cmd = Assert.IsType<ContinueCommand>(res.Command);
        Assert.Equal(ValidRunId, cmd.RunId);
    }

    [Fact]
    public void ParseContinue_RejectsUnknownSwitch()
    {
        var args = new[] { "agent", "continue", ValidRunId, "--json" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("unknown_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseStatus_Succeeds()
    {
        var args = new[] { "agent", "status", ValidRunId };
        var res = AgentCliParser.Parse(args);
        Assert.True(res.Success);
        var cmd = Assert.IsType<StatusCommand>(res.Command);
        Assert.False(cmd.Json);
    }

    [Fact]
    public void ParseStatus_SucceedsWithJson()
    {
        var args = new[] { "agent", "status", ValidRunId, "--json" };
        var res = AgentCliParser.Parse(args);
        Assert.True(res.Success);
        var cmd = Assert.IsType<StatusCommand>(res.Command);
        Assert.True(cmd.Json);
    }

    [Fact]
    public void ParseStatus_RejectsUnknownSwitch()
    {
        var args = new[] { "agent", "status", ValidRunId, "--foobar" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("unknown_switch", res.ErrorCode);
    }

    [Fact]
    public void ParseStatus_RejectsMissingRunId()
    {
        var args = new[] { "agent", "status" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("missing_run_id", res.ErrorCode);
    }

    [Fact]
    public void Parser_RejectsUnknownSubcommand()
    {
        var args = new[] { "agent", "foobar", ValidRunId };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("unknown_subcommand", res.ErrorCode);
    }

    [Fact]
    public void Parser_RejectsUnknownTopLevelCommand()
    {
        var args = new[] { "agents", "run", "--workspace", ValidWorkspace, "prompt" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("unknown_command", res.ErrorCode);
    }

    [Fact]
    public void Parser_RejectsTooManyArguments()
    {
        var args = new string[65];
        args[0] = "agent";
        args[1] = "run";
        for (int i = 2; i < args.Length; i++) args[i] = "--json";
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("too_many_arguments", res.ErrorCode);
    }

    [Fact]
    public void Parser_RejectsInvalidRunIdFormat()
    {
        var args = new[] { "agent", "attach", "invalid-run-id" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("invalid_run_id", res.ErrorCode);
    }

    [Fact]
    public void Parser_BoundedPromptLengthRejected()
    {
        var longPrompt = new string('x', AgentCliParser.MaxPromptLength + 1);
        var args = new[] { "agent", "run", "--workspace", ValidWorkspace, longPrompt };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.True(res.ErrorCode == "prompt_too_long" || res.ErrorCode == "argument_too_long");
    }

    [Fact]
    public void Parser_BoundedWorkspacePathRejected()
    {
        var longPath = new string('a', AgentCliParser.MaxWorkspacePathLength + 1);
        var args = new[] { "agent", "run", "--workspace", longPath, "prompt" };
        var res = AgentCliParser.Parse(args);
        Assert.False(res.Success);
        Assert.Equal("invalid_workspace", res.ErrorCode);
    }
}
