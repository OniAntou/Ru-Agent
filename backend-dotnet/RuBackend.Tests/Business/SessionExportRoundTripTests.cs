using Microsoft.Extensions.DependencyInjection;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuAgent.SessionInterchange;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class SessionExportRoundTripTests
{
    [Fact]
    public async Task Export_and_import_cover_the_full_history_beyond_GetAsync_window()
    {
        using var factory = new BackendWebApplicationFactory();
        using var http = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ISessionStore>();
        var id = (await store.CreateAsync(CancellationToken.None))!;
        var messages = Enumerable.Range(0, 450)
            .Select(index => new SessionMessage(index % 2 == 0 ? "user" : "ai", $"message-{index:D3}"))
            .ToArray();
        Assert.True(await store.AppendAsync(id, messages, CancellationToken.None));
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var session = PublicHostSession.FromDescriptor(new RuntimeDescriptor(1, http.BaseAddress!.AbsoluteUri,
            "test-secret", process.Id, process.StartTime.ToUniversalTime().ToString("O"),
            new string('a', 32), DateTimeOffset.UtcNow.ToString("O")));
        var api = new SessionInterchangeApiClient(session, http);

        var exported = await api.ExportAsync(id, "claude-code");
        var content = Assert.Single(exported.Files).Content;
        var transcript = SessionFormatCatalog.Find("claude-code")!.Read(content);
        Assert.Equal(450, transcript.Entries.Count);
        Assert.Equal("message-000", transcript.Entries[0].Text);
        Assert.Equal("message-449", transcript.Entries[^1].Text);

        var imported = await api.ImportAsync("claude-code", content);

        Assert.Equal(450, imported.Messages);
        Assert.Equal(0, imported.DroppedEntries);
        var importedDocument = await store.GetAsync(imported.SessionId, CancellationToken.None);
        Assert.Equal(450, importedDocument!.TotalMessageCount);
        Assert.Equal(250, importedDocument.MessageOffset);
        Assert.Equal("message-449", importedDocument.Messages[^1].Content);
    }

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
