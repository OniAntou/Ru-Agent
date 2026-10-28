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
}
