using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentSessionContractTests
{
    [Fact]
    public void Agent_request_starts_without_claiming_a_grant()
    {
        var service = new AgentSessionService();
        service.ObserveSession("session-1");
        service.BeginRequest("session-1");

        Assert.True(service.IsEnabled);
        Assert.False(service.HasActualGrant);
        Assert.Equal("session-1", service.SessionId);
        Assert.Empty(service.Scopes);

        service.ApplyGrant(new AgentSessionGrantModel("session-1", new[] { "system.write" }));

        Assert.True(service.HasActualGrant);
        Assert.Equal(new[] { "system.write" }, service.Scopes);
    }

    [Fact]
    public void Normal_chat_can_display_an_actual_grant_without_enabling_Agent_mode()
    {
        var service = new AgentSessionService();
        service.ObserveSession("session-1");

        service.ApplyGrant(new AgentSessionGrantModel("session-1", new[] { "system.write" }));

        Assert.False(service.IsEnabled);
        Assert.True(service.HasActualGrant);
        Assert.Equal(new[] { "system.write" }, service.Scopes);
    }

    [Fact]
    public void Grant_for_a_different_observed_session_is_rejected()
    {
        var service = new AgentSessionService();
        service.ObserveSession("session-1");

        Assert.Throws<InvalidOperationException>(() =>
            service.ApplyGrant(new AgentSessionGrantModel("session-2", new[] { "system.write" })));
        Assert.False(service.HasActualGrant);
        Assert.Empty(service.Scopes);
    }

    [Fact]
    public void Disable_clears_mode_and_grant_but_preserves_the_observed_session()
    {
        var service = new AgentSessionService();
        service.ObserveSession("session-1");
        service.BeginRequest("session-1");
        service.ApplyGrant(new AgentSessionGrantModel("session-1", new[] { "system.write" }));

        service.Disable();

        Assert.False(service.IsEnabled);
        Assert.False(service.HasActualGrant);
        Assert.Equal("session-1", service.SessionId);
        Assert.Empty(service.Scopes);

        service.ApplyGrant(new AgentSessionGrantModel("session-1", new[] { "system.write" }));
        Assert.True(service.HasActualGrant);
    }

    [Fact]
    public void Observing_a_new_session_clears_the_old_grant()
    {
        var service = new AgentSessionService();
        service.ObserveSession("session-1");
        service.ApplyGrant(new AgentSessionGrantModel("session-1", new[] { "system.write" }));

        service.ObserveSession("session-2");

        Assert.Equal("session-2", service.SessionId);
        Assert.False(service.HasActualGrant);
        Assert.Empty(service.Scopes);
    }

    [Fact]
    public void Beginning_another_request_clears_the_previous_grant()
    {
        var service = new AgentSessionService();
        service.ObserveSession("session-1");
        service.ApplyGrant(new AgentSessionGrantModel("session-1", new[] { "system.write" }));

        service.BeginRequest("session-1");

        Assert.True(service.IsEnabled);
        Assert.False(service.HasActualGrant);
        Assert.Empty(service.Scopes);
    }

    [Fact]
    public void Mode_starts_at_chat_and_begin_request_selects_agent()
    {
        var service = new AgentSessionService();
        service.ObserveSession("session-1");

        Assert.Equal(RuAppMode.Chat, service.CurrentMode);
        Assert.False(service.IsEnabled);

        service.BeginRequest("session-1");

        Assert.Equal(RuAppMode.Agent, service.CurrentMode);
        Assert.True(service.IsEnabled);
    }

    [Fact]
    public void Begin_request_with_full_selects_the_full_mode()
    {
        var service = new AgentSessionService();
        service.ObserveSession("session-1");

        service.BeginRequest("session-1", RuAppMode.Full);

        Assert.Equal(RuAppMode.Full, service.CurrentMode);
        Assert.True(service.IsEnabled);
        Assert.False(service.HasActualGrant);
    }

    [Fact]
    public void Disable_returns_the_mode_to_chat()
    {
        var service = new AgentSessionService();
        service.ObserveSession("session-1");
        service.BeginRequest("session-1", RuAppMode.Full);

        service.Disable();

        Assert.Equal(RuAppMode.Chat, service.CurrentMode);
        Assert.False(service.IsEnabled);
        Assert.Equal("session-1", service.SessionId);
    }

    [Fact]
    public void Full_access_confirmation_is_stored_once_per_service()
    {
        var service = new AgentSessionService();

        Assert.False(service.HasConfirmedFullAccess);

        service.ConfirmFullAccess();

        Assert.True(service.HasConfirmedFullAccess);
    }
}
