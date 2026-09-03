using RuAgent.Client;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentSessionRecoveryTests
{
    [Fact]
    public void Recovery_is_one_shot_per_stale_session_and_resets_for_a_new_turn()
    {
        var recovery = new AgentSessionRecovery();

        Assert.True(recovery.TryBegin("stale-session"));
        Assert.False(recovery.TryBegin("stale-session"));
        Assert.True(recovery.TryBegin("another-session"));

        recovery.Reset();

        Assert.True(recovery.TryBegin("stale-session"));
    }

    [Theory]
    [InlineData("session_not_found")]
    [InlineData("invalid_session_id")]
    public void Only_stale_session_errors_are_recoverable(string errorCode)
    {
        Assert.True(AgentSessionRecovery.IsRecoverable(
            new AgentApiException(errorCode, "stale")));
        Assert.False(AgentSessionRecovery.IsRecoverable(
            new AgentApiException("workspace_busy", "busy")));
    }

    [Fact]
    public void ChatView_retries_a_stale_session_only_through_the_recovery_helper()
    {
        var source = File.ReadAllText(FindChatViewPath());

        Assert.Contains("AgentSessionRecovery.IsRecoverable", source, StringComparison.Ordinal);
        Assert.Contains("allowSessionRecovery", source, StringComparison.Ordinal);
        Assert.Contains("RecoverAgentSessionAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatView_waits_for_the_previous_agent_run_to_cancel_before_switching_session()
    {
        var source = File.ReadAllText(FindChatViewPath());
        var openSession = source.IndexOf("public async Task OpenSessionAsync", StringComparison.Ordinal);
        var switchSession = source.IndexOf("_activeSessionId = sessionId;", openSession, StringComparison.Ordinal);

        Assert.True(openSession >= 0);
        Assert.True(switchSession > openSession);
        var stopBeforeSwitch = source.LastIndexOf("await StopActiveAgentAsync(force: true);", switchSession, StringComparison.Ordinal);
        Assert.True(
            stopBeforeSwitch > openSession,
            "Switching sessions must await cancellation of the previous run before changing the active session.");
    }

    private static string FindChatViewPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "gui-winui3", "Views", "ChatView.xaml.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate ChatView.xaml.cs.");
    }
}
