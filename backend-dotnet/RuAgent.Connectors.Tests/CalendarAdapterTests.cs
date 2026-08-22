using RuAgent.Connectors;
using RuAgent.Connectors.Calendar;
using RuAgent.Connectors.Credentials;
using Xunit;

namespace RuAgent.Connectors.Tests;

public sealed class CalendarAdapterTests
{
    [Fact]
    public async Task Calendar_preview_is_side_effect_free_and_create_requires_exact_approval()
    {
        var gateway = new FakeCalendarGateway();
        var adapter = new CalendarAdapter(
            new ConnectorCredentialStore(new Dictionary<string, string> { ["calendar"] = "token" }),
            gateway);
        var session = Session();
        var draft = new CalendarEventDraft("default", "Focus", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2));
        var preview = adapter.PreviewCreate(session, draft);
        Assert.Equal("preview", preview.Status);
        Assert.Equal(0, gateway.Calls);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.CreateAsync(
            session,
            draft,
            new ConnectorApproval("approval", session.SessionId, "wrong", true),
            CancellationToken.None));
        var result = await adapter.CreateAsync(
            session,
            draft,
            new ConnectorApproval("approval", session.SessionId, preview.Output["arguments_hash"]!.GetValue<string>(), true),
            CancellationToken.None);
        Assert.Equal("succeeded", result.Status);
        Assert.Equal(1, gateway.Calls);
    }

    private static ConnectorSession Session() => new(
        "calendar",
        "session-1",
        new HashSet<string>(["calendar.write"]),
        DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class FakeCalendarGateway : ICalendarGateway
    {
        public int Calls { get; private set; }
        public Task<string> CreateAsync(string accessToken, CalendarEventDraft draft, CancellationToken cancellationToken)
        {
            Assert.Equal("token", accessToken);
            Calls++;
            return Task.FromResult("event-1");
        }
    }
}
