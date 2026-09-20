using RuAgent.SessionInterchange;
using Xunit;

namespace RuAgent.SessionInterchange.Tests;

public sealed class SessionFileLayoutTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 9, 20, 9, 30, 0, TimeSpan.Zero);
    private const string Home = @"C:\Users\Someone";

    [Fact]
    public void A_claude_code_transcript_lands_in_the_directory_named_after_its_project()
    {
        var targets = SessionFileLayout.TargetsFor(
            SessionFileLayout.ClaudeCodeId,
            ["11111111-1111-7111-8111-111111111111.jsonl"],
            @"D:\roulette-quiz",
            Stamp,
            Home);

        var target = Assert.Single(targets);
        Assert.Equal(
            Path.Combine(Home, ".claude", "projects", "D--roulette-quiz", "11111111-1111-7111-8111-111111111111.jsonl"),
            target.Path);
        Assert.False(target.Append);
    }

    [Theory]
    [InlineData(@"D:\roulette-quiz", "D--roulette-quiz")]
    [InlineData(@"D:\LLM\", "D--LLM")]
    [InlineData("/home/someone/project", "-home-someone-project")]
    [InlineData("", "unknown-project")]
    [InlineData(null, "unknown-project")]
    public void The_project_directory_is_the_working_directory_with_its_separators_replaced(string? workingDirectory, string expected)
    {
        Assert.Equal(expected, SessionFileLayout.ProjectSlug(workingDirectory));
    }

    [Fact]
    public void A_codex_export_files_its_rollout_by_date_and_appends_to_the_picker_index()
    {
        var targets = SessionFileLayout.TargetsFor(
            SessionFileLayout.CodexId,
            ["rollout-2026-09-20T09-30-00-11111111-1111-7111-8111-111111111111.jsonl", "session_index.jsonl"],
            @"D:\LLM",
            Stamp,
            Home);

        Assert.Equal(2, targets.Count);
        Assert.Equal(
            Path.Combine(Home, ".codex", "sessions", "2026", "09", "20", "rollout-2026-09-20T09-30-00-11111111-1111-7111-8111-111111111111.jsonl"),
            targets[0].Path);
        Assert.False(targets[0].Append);

        // The index holds every thread there is, so an export adds a line to it instead of
        // replacing it with the one session it knows about.
        Assert.Equal(Path.Combine(Home, ".codex", "session_index.jsonl"), targets[1].Path);
        Assert.True(targets[1].Append);
    }

    [Theory]
    [InlineData(@"C:\Users\Someone\.codex\sessions\2026\09\07\rollout-2026-09-07T22-13-44-01a07c6e-aff5-7fa3-b7f6-9e7cd8096ae4.jsonl", "codex")]
    [InlineData(@"C:\Users\Someone\.claude\projects\D--LLM\75127d8e-65c4-4981-88ba-3048fbf0d494.jsonl", "claude-code")]
    [InlineData(@"C:\Users\Someone\.gemini\antigravity\brain\c64adfae-1c56-4daa-8693-58b8d5401900\.system_generated\logs\transcript.jsonl", "antigravity")]
    [InlineData("session.jsonl", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_session_file_names_the_agent_that_wrote_it(string? path, string? expected)
    {
        // A name that says nothing gets no answer: guessing a format would read the file as
        // the wrong thing and then refuse it, which looks like the file's fault.
        Assert.Equal(expected, SessionFileLayout.HarnessFromFileName(path));
    }

    [Fact]
    public void A_format_with_no_known_home_is_refused_rather_than_placed_somewhere()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => SessionFileLayout.TargetsFor(
            "cursor",
            ["session.jsonl"],
            @"D:\LLM",
            Stamp,
            Home));

        Assert.Contains("cursor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_export_with_no_files_has_nowhere_to_go()
    {
        Assert.Throws<ArgumentException>(() => SessionFileLayout.TargetsFor(
            SessionFileLayout.ClaudeCodeId,
            [],
            @"D:\LLM",
            Stamp,
            Home));
    }
}
