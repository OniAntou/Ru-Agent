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

    [Theory]
    [InlineData("session_not_found")]
    [InlineData("invalid_session_id")]
    public async Task Recovery_replaces_the_session_before_replaying_the_turn(string errorCode)
    {
        var recovery = new AgentSessionRecovery();
        var order = new List<string>();
        var accepted = await AgentSessionRecovery.TryRecoverAsync(
            new AgentApiException(errorCode, "stale"),
            true,
            "stale-session",
            () => true,
            () => recovery.TryBegin("stale-session"),
            () => { order.Add("replace"); return Task.CompletedTask; },
            () => { order.Add("replay"); return Task.FromResult(true); });

        Assert.Equal(true, accepted);
        Assert.Equal(new[] { "replace", "replay" }, order);
    }

    [Fact]
    public async Task Recovery_requires_an_enabled_current_session_and_a_successful_one_shot_claim()
    {
        int attempts = 0;
        Task<bool>? replay = null;
        var stale = new AgentApiException("session_not_found", "stale");
        var busy = new AgentApiException("workspace_busy", "busy");

        Assert.Null(await AgentSessionRecovery.TryRecoverAsync(stale, false, "s", () => true,
            () => { attempts++; return true; }, () => Task.CompletedTask,
            () => replay = Task.FromResult(true)));
        Assert.Null(await AgentSessionRecovery.TryRecoverAsync(stale, true, "s", () => false,
            () => { attempts++; return true; }, () => Task.CompletedTask,
            () => replay = Task.FromResult(true)));
        Assert.Null(await AgentSessionRecovery.TryRecoverAsync(busy, true, "s", () => true,
            () => { attempts++; return true; }, () => Task.CompletedTask,
            () => replay = Task.FromResult(true)));
        Assert.Null(await AgentSessionRecovery.TryRecoverAsync(stale, true, "s", () => true,
            () => { attempts++; return false; }, () => Task.CompletedTask,
            () => replay = Task.FromResult(true)));

        Assert.Equal(1, attempts);
        Assert.Null(replay);
    }

}
