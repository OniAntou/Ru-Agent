using RuAgent.Connectors;
using RuAgent.Connectors.Credentials;
using RuAgent.Connectors.Mail;
using Xunit;

namespace RuAgent.Connectors.Tests;

public sealed class MailAdapterTests
{
    [Fact]
    public async Task Mail_token_stays_inside_gateway_and_wrong_hash_has_no_side_effect()
    {
        var gateway = new FakeMailGateway();
        var adapter = new MailAdapter(
            new ConnectorCredentialStore(new Dictionary<string, string> { ["mail"] = "secret-token" }),
            gateway);
        var session = new ConnectorSession("mail", "session-1", new HashSet<string>(["mail.send"]), DateTimeOffset.UtcNow.AddMinutes(1));
        var draft = new MailDraft("a@example.com", "Hi", "Body");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.SendAsync(
            session,
            draft,
            new ConnectorApproval("approval", session.SessionId, "changed", true),
            CancellationToken.None));
        Assert.Equal(0, gateway.Calls);
    }

    private sealed class FakeMailGateway : IMailGateway
    {
        public int Calls { get; private set; }
        public Task<string> SendAsync(string accessToken, MailDraft draft, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.DoesNotContain("secret-token", draft.Body, StringComparison.Ordinal);
            return Task.FromResult("message-1");
        }
    }
}
