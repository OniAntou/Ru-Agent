using Microsoft.Extensions.DependencyInjection;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuAgent.SessionInterchange;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class SessionExportRoundTripTests
{
    [Theory]
    [InlineData("claude-code")]
    [InlineData("codex")]
    public async Task Persisted_large_session_exports_through_the_real_host_and_client(string harness)
    {
        using var factory = new BackendWebApplicationFactory();
        using var http = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ISessionStore>();
        var id = (await store.CreateAsync(CancellationToken.None))!;
        // Escaped characters exercise the nested JSON envelope, not just raw text size.
        var text = new string('x', 600_000) + new string('<', 40_000) + new string('"', 40_000);
        var messages = Enumerable.Range(0, 10).Select(index => new SessionMessage(index % 2 == 0 ? "user" : "ai", text)).ToArray();
        Assert.True(await store.AppendAsync(id, messages, CancellationToken.None));
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var session = PublicHostSession.FromDescriptor(new RuntimeDescriptor(1, http.BaseAddress!.AbsoluteUri,
            "test-secret", process.Id, process.StartTime.ToUniversalTime().ToString("O"),
            new string('a', 32), DateTimeOffset.UtcNow.ToString("O")));

        var result = await new SessionInterchangeApiClient(session, http).ExportAsync(id, harness);

        var file = result.Files.First(file => file.Name != CodexSessionFormat.IndexFileName);
        var transcript = SessionFormatCatalog.Find(harness)!.Read(file.Content);
        Assert.Equal(messages.Length, transcript.Entries.Count);
        Assert.All(transcript.Entries, entry => Assert.Equal(text, entry.Text));
    }
}
