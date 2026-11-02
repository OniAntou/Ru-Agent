using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class AgentSlashCommandSurfaceTests
{
    [Theory]
    [InlineData("/model", true)]
    [InlineData("/model", false)]
    [InlineData("/backend", true)]
    [InlineData("/reasoning", true)]
    public async Task Cancelled_picker_does_not_append_a_text_option_panel_or_save_settings(string command, bool liveCatalog)
    {
        var output = new StringWriter(); var posted = new List<string>();
        var catalog = JsonNode.Parse(ReasoningCatalog)!.AsObject();
        catalog["catalog"]!.AsArray().Add(new JsonObject { ["id"] = "synthetic-alternate-model", ["supported"] = true });
        var configured = JsonNode.Parse(ReasoningSettings)!.AsObject();
        configured["anthropic_model"] = "synthetic-fallback-model";
        var commands = Create(output, request => {
            if (!liveCatalog && request.RequestUri!.AbsolutePath.EndsWith("/models")) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (request.RequestUri!.AbsolutePath == "/settings") return Json(configured.ToJsonString());
            return Router(request, catalog.ToJsonString(), posted,
                """{"status":"ok","providers":[{"id":"openrouter","name":"OpenRouter"}]}""");
        });
        var calls = 0; string? beforeCancel = null;
        commands.Picker = (_, _) => {
            calls++; beforeCancel = output.ToString(); return Task.FromResult<string?>(null);
        };
        Assert.True(await commands.TryHandleAsync(command, null, "sess_1", "standard", "D:\\LLM", default));
        Assert.Equal(1, calls);
        Assert.Equal(beforeCancel, output.ToString());
        Assert.Empty(posted);
    }

    [Theory]
    [InlineData("/model", "synthetic-alternate-model")]
    [InlineData("/backend", "anthropic")]
    [InlineData("/reasoning", "high")]
    public async Task Cancelled_token_after_a_picker_answer_never_saves_or_lists_options(string command, string answer)
    {
        var output = new StringWriter(); var posted = new List<string>(); using var stop = new CancellationTokenSource();
        var catalog = JsonNode.Parse(ReasoningCatalog)!.AsObject();
        catalog["catalog"]!.AsArray().Add(new JsonObject { ["id"] = "synthetic-alternate-model", ["supported"] = true });
        var commands = Create(output, request => Router(request, catalog.ToJsonString(), posted));
        string? beforeCancel = null;
        commands.Picker = (_, _) => { beforeCancel = output.ToString(); stop.Cancel(); return Task.FromResult<string?>(answer); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commands.TryHandleAsync(command, null, stop.Token));
        Assert.Empty(posted); Assert.Equal(beforeCancel, output.ToString());
    }
}
