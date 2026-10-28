using System.Net;
using System.Text;
using RuAgent.Cli;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class SkillsSlashCommandTests
{
    [Fact]
    public async Task Skills_lists_metadata_and_diagnostics_without_a_run_id()
    {
        var session = TestSession();
        var settings = new SettingsApiClient(session, new HttpClient(new JsonHandler("{}")));
        var skills = new SkillsApiClient(session, new HttpClient(new JsonHandler("""
            {"items":[{"id":"review","name":"Review","description":"Review changes","source_path":"D:\\skills\\review","resource_base":"D:\\skills\\review","revision":"r1","enabled":true,"model_invocable":false,"user_invocable":false,"requirements":[],"compatibility":null}],"diagnostics":[{"source_path":"D:\\skills\\broken","code":"invalid_metadata"}],"truncated":false,"total":1}
            """)));
        var output = new StringWriter();
        var commands = new AgentSlashCommands(settings, null, output, new StringWriter(), skills);

        Assert.True(await commands.TryHandleAsync("/skills", null, null, "standard", "D:\\LLM", CancellationToken.None));

        Assert.Contains("review — Review [enabled, not-user-invocable, not-model-invocable]", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("invalid_metadata", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skills_install_uses_the_explicit_pinned_source_and_prints_receipt()
    {
        const string source = "https://github.com/acme/repo/tree/0123456789abcdef0123456789abcdef01234567/skills/demo";
        var handler = new InstallHandler("""
            {"receipt":{"action":"installed","id":"demo","name":"Demo","description":"Package","source":"https://github.com/acme/repo/tree/0123456789abcdef0123456789abcdef01234567/skills/demo","commit":"0123456789abcdef0123456789abcdef01234567","packageSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","fileCount":2,"expandedBytes":34,"installPath":"C:\\Users\\test\\AppData\\Local\\Ru\\skills\\demo"},"catalog":{"items":[],"diagnostics":[],"truncated":false,"total":0}}
            """);
        var settings = new SettingsApiClient(TestSession(), new HttpClient(new JsonHandler("{}")));
        var skills = new SkillsApiClient(TestSession(), new HttpClient(handler));
        var output = new StringWriter();
        var commands = new AgentSlashCommands(settings, null, output, new StringWriter(), skills);

        Assert.True(await commands.TryHandleAsync("/skills", $"install {source}", null, "standard", "D:\\LLM", CancellationToken.None));

        Assert.EndsWith("agent/v1/skills/packages", handler.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("install", handler.Body!.RootElement.GetProperty("action").GetString());
        Assert.Equal(source, handler.Body.RootElement.GetProperty("source_url").GetString());
        Assert.Contains("installed: demo", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(new string('a', 64), output.ToString(), StringComparison.Ordinal);
    }

    private static PublicHostSession TestSession()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        return PublicHostSession.FromDescriptor(new RuntimeDescriptor(
            1, "http://127.0.0.1:8000/", new string('e', 64), process.Id,
            process.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O")));
    }

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class InstallHandler(string body) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public System.Text.Json.JsonDocument? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
