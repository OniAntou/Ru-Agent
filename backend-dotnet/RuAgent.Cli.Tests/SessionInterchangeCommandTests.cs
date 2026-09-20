using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// The command surface of session interchange: which arguments are required, and which
/// mistakes are answered rather than ignored.
/// </summary>
public sealed class SessionInterchangeCommandTests
{
    [Fact]
    public void Session_export_takes_a_session_id_and_the_format_that_was_asked_for()
    {
        var result = AgentCliParser.Parse(["agent", "session", "export", "s1", "--harness", "codex"]);

        Assert.True(result.Success);
        var command = Assert.IsType<SessionExportCommand>(result.Command);
        Assert.Equal("s1", command.SessionId);
        Assert.Equal("codex", command.Harness);
        Assert.Null(command.OutputDirectory);
        Assert.False(command.Json);
    }

    [Fact]
    public void Session_export_takes_an_output_directory_and_json()
    {
        var result = AgentCliParser.Parse(
            ["agent", "session", "export", "s1", "--harness", "claude-code", "--out", @"C:\tmp", "--json"]);

        var command = Assert.IsType<SessionExportCommand>(result.Command);
        Assert.Equal(@"C:\tmp", command.OutputDirectory);
        Assert.True(command.Json);
    }

    [Theory]
    [InlineData("missing_session_id")]
    [InlineData("missing_harness")]
    public void Session_export_names_what_it_is_missing(string _)
    {
        // A format is not assumed: writing a session into the wrong agent's store is a
        // mistake that looks like success.
        var withoutHarness = AgentCliParser.Parse(["agent", "session", "export", "s1"]);
        Assert.False(withoutHarness.Success);
        Assert.Equal("missing_harness", withoutHarness.ErrorCode);

        var withoutSession = AgentCliParser.Parse(["agent", "session", "export", "--harness", "codex"]);
        Assert.False(withoutSession.Success);
        Assert.Equal("missing_session_id", withoutSession.ErrorCode);
    }

    [Fact]
    public void Session_import_takes_a_file_an_optional_harness_and_a_session()
    {
        var bare = AgentCliParser.Parse(["agent", "session", "import", "session.jsonl"]);
        var command = Assert.IsType<SessionImportCommand>(bare.Command);
        Assert.Equal("session.jsonl", command.FilePath);
        Assert.Null(command.Harness);
        Assert.Null(command.SessionId);

        var named = AgentCliParser.Parse(
            ["agent", "session", "import", "opencode.db", "--harness", "opencode", "--session", "ses_1", "--json"]);
        var namedCommand = Assert.IsType<SessionImportCommand>(named.Command);

        // opencode keeps every session in one database, so the store is the file and the
        // session has to be nameable.
        Assert.Equal("opencode", namedCommand.Harness);
        Assert.Equal("ses_1", namedCommand.SessionId);
        Assert.True(namedCommand.Json);
    }

    [Fact]
    public void Session_import_requires_a_file_and_rejects_unknown_switches()
    {
        var withoutFile = AgentCliParser.Parse(["agent", "session", "import", "--harness", "codex"]);
        Assert.False(withoutFile.Success);
        Assert.Equal("missing_file", withoutFile.ErrorCode);

        var unknown = AgentCliParser.Parse(["agent", "session", "import", "session.jsonl", "--deep"]);
        Assert.False(unknown.Success);
        Assert.Equal("unknown_switch", unknown.ErrorCode);
    }

    [Fact]
    public void The_session_subcommand_list_includes_both_directions()
    {
        var result = AgentCliParser.Parse(["agent", "session", "teleport"]);

        Assert.False(result.Success);
        Assert.Equal("unknown_subcommand", result.ErrorCode);
        Assert.Contains("export", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains("import", result.ErrorMessage!, StringComparison.Ordinal);
    }
}
