using RuAgent.Cli;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>One-shot errors read as code plus message, never raw JSON envelopes.</summary>
public sealed class CliErrorTextTests
{
    [Fact]
    public void Format_PrefersTheServerMessageOverRawJson()
    {
        var ex = new AgentApiException(
            "catalog_unauthorized",
            "{\"error_code\":\"catalog_unauthorized\",\"message\":\"Provider key rejected.\"}");

        Assert.Equal("catalog_unauthorized: Provider key rejected.", CliErrorText.Format(ex));
    }

    [Fact]
    public void Format_FallsBackToTruncatedBodyWhenNoMessageField()
    {
        var ex = new AgentApiException("conflict", "not-json{{{");

        Assert.Equal("conflict: not-json{{{", CliErrorText.Format(ex));
    }

    [Fact]
    public void Format_CollapsesNewlinesAndTruncatesLongBodies()
    {
        var ex = new AgentApiException("agent_create_failed", "line one\nline two " + new string('x', 300));

        var text = CliErrorText.Format(ex);

        Assert.StartsWith("agent_create_failed: line one line two ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text, StringComparison.Ordinal);
        Assert.True(text.Length <= "agent_create_failed: ".Length + CliErrorText.MaxMessageChars);
    }

    [Fact]
    public void Format_NamesEmptyBodies()
    {
        var ex = new AgentApiException("timeout", "   ");

        Assert.Equal("timeout: request failed", CliErrorText.Format(ex));
    }
}
