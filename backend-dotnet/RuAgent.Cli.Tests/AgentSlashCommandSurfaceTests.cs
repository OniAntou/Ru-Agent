using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Cli;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// The slash-command surfaces: the pickers must be offered when one is wired, and the text
/// panels must stand in when there is no interactive console (which is what these tests see,
/// since a test host has no keyboard).
/// </summary>
public sealed class AgentSlashCommandSurfaceTests
{
    private static PublicHostSession Session()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(
            1,
            "http://127.0.0.1:8000/",
            "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            proc.Id,
            proc.StartTime.ToUniversalTime().ToString("O"),
            new string('a', 32),
            DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(desc);
    }

    private static AgentSlashCommands Create(
        StringWriter output,
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
        string settingsJson = "{\"status\":\"ok\",\"backend\":\"OpenAI\",\"openai_model\":\"gpt-4o\",\"has_openai_api_key\":true,\"anthropic_model\":\"claude-sonnet-4-5\"}")
    {
        var client = new SettingsApiClient(
            Session(),
            new HttpClient(new FakeHandler(respond ?? (_ => Json(settingsJson)))));
        return new AgentSlashCommands(client, output, new StringWriter()) { PanelWidth = 80 };
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Help_RendersTheCommandPanel()
    {
        var output = new StringWriter();
        var commands = Create(output);

        Assert.True(await commands.TryHandleAsync("/help", null, null, "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.Contains("╭─ help", text, StringComparison.Ordinal);
        Assert.Contains("/model", text, StringComparison.Ordinal);
        Assert.Contains("/quit", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_RendersTheCardWithSettingsAndCounters()
    {
        var output = new StringWriter();
        var commands = Create(output);
        commands.SnapshotProvider = () => new TuiStatusSnapshot(
            "sess_1", "standard", "Kilo Gateway", "kilocode/gpt-5-codex", "D:\\LLM", 3, 12400, 4, true);

        Assert.True(await commands.TryHandleAsync("/status", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.Contains("╭─ status", text, StringComparison.Ordinal);
        Assert.Contains("12400", text, StringComparison.Ordinal);
        Assert.Contains("gpt-4o", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stats_And_Tokens_ShareTheStatusCard()
    {
        foreach (var command in new[] { "/stats", "/tokens" })
        {
            var output = new StringWriter();
            var commands = Create(output);

            Assert.True(await commands.TryHandleAsync(command, null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

            Assert.Contains("╭─ status", output.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Model_WithoutAPicker_ListsTheConfiguredModels()
    {
        var output = new StringWriter();
        var commands = Create(output);

        Assert.True(await commands.TryHandleAsync("/model", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.Contains("╭─ model", text, StringComparison.Ordinal);
        Assert.Contains("gpt-4o", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_WithAPicker_UsesTheChosenModel()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return Json("{\"status\":\"ok\"}");
            }

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\",\"openai_model\":\"gpt-4o\",\"anthropic_model\":\"claude-sonnet-4-5\"}");
        });

        ChoiceList? offered = null;
        commands.Picker = (list, _) =>
        {
            offered = list;
            return Task.FromResult<string?>("claude-sonnet-4-5");
        };

        Assert.True(await commands.TryHandleAsync("/model", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.NotNull(offered);
        Assert.Equal(2, offered!.Items.Count);
        Assert.Equal("claude-sonnet-4-5", offered.Items[1].Value);
        Assert.Contains("Switched active model to: claude-sonnet-4-5", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("openai_model", posted.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_WithASingleCandidate_SkipsThePicker()
    {
        var output = new StringWriter();
        var commands = Create(output, _ => Json("{\"status\":\"ok\",\"backend\":\"OpenAI\",\"openai_model\":\"gpt-4o\"}"));
        var pickerCalls = 0;
        commands.Picker = (_, _) =>
        {
            pickerCalls++;
            return Task.FromResult<string?>(null);
        };

        Assert.True(await commands.TryHandleAsync("/model", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.Equal(0, pickerCalls);
        Assert.Contains("gpt-4o", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backend_WithAPicker_SwitchesToTheChosenBackend()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return Json("{\"status\":\"ok\"}");
            }

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\",\"openai_model\":\"gpt-4o\"}");
        });
        commands.Picker = (list, _) => Task.FromResult<string?>(list.Items[1].Value);

        Assert.True(await commands.TryHandleAsync("/backend", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.Contains("Anthropic", posted.Single(), StringComparison.Ordinal);
        Assert.Contains("Switched backend to: Anthropic", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backend_WithoutAPicker_ListsEveryBackend()
    {
        var output = new StringWriter();
        var commands = Create(output);

        Assert.True(await commands.TryHandleAsync("/backend", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.Contains("╭─ backend", text, StringComparison.Ordinal);
        foreach (var name in new[]
        {
            "OpenAI",
            "Anthropic",
            "Custom OpenAI",
            "Models.dev: OpenCode Zen",
            "Models.dev: Kilo Gateway",
            "Models.dev: Cloudflare Workers AI",
        })
        {
            Assert.Contains(name, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Backend_WithoutAPicker_MarksProvidersWithoutAKey()
    {
        var output = new StringWriter();
        var commands = Create(output, _ => Json(
            "{\"status\":\"ok\",\"backend\":\"OpenAI\",\"openai_model\":\"gpt-4o\"," +
            "\"has_openai_api_key\":false,\"has_anthropic_api_key\":true}"));

        Assert.True(await commands.TryHandleAsync("/backend", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.Contains("no key", text, StringComparison.Ordinal);
        Assert.Contains("OpenAI", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Anthropic · no key", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backend_WithAnArgument_SwitchesDirectly()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return Json("{\"status\":\"ok\"}");
            }

            return Json("{\"status\":\"ok\",\"backend\":\"Kilo Gateway\"}");
        });

        await commands.TryHandleAsync("/backend", "anthropic", "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        Assert.Contains("Anthropic", posted.Single(), StringComparison.Ordinal);
        Assert.Contains("Switched backend to: Anthropic", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_WithAnArgument_SwitchesDirectly()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return Json("{\"status\":\"ok\"}");
            }

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\",\"openai_model\":\"old\"}");
        });

        await commands.TryHandleAsync("/model", "new-model", "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        Assert.Contains("new-model", posted.Single(), StringComparison.Ordinal);
        Assert.Contains("Switched active model to: new-model", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnhandledSlashCommand_FallsThrough()
    {
        var commands = Create(new StringWriter());

        Assert.False(await commands.TryHandleAsync("/nope", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));
    }

    [Fact]
    public async Task Status_WithoutASnapshotProvider_StillRenders()
    {
        var output = new StringWriter();
        var commands = Create(output);

        await commands.TryHandleAsync("/status", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        var text = output.ToString();
        Assert.Contains("╭─ status", text, StringComparison.Ordinal);
        Assert.Contains("sess_1", text, StringComparison.Ordinal);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _func;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = func;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_func(request));
    }
}
