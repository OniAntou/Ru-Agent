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
    public async Task Backend_WithBareOpenrouter_SwitchesToModelsDev()
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

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\"}");
        });

        await commands.TryHandleAsync("/backend", "openrouter", "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        Assert.Contains("models.dev:openrouter", posted.Single(), StringComparison.Ordinal);
        Assert.Contains("Switched backend to: models.dev:openrouter", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_OnOpenrouterBackend_OffersTheCurrentModelFirst()
    {
        var output = new StringWriter();
        var commands = Create(output, _ => Json(
            "{\"status\":\"ok\",\"backend\":\"models.dev:openrouter\",\"model\":\"cohere/north-mini-code:free\",\"openai_model\":\"gpt-4o\"}"));
        IReadOnlyList<ChoiceItem>? offered = null;
        commands.Picker = (list, _) => { offered = list.Items; return Task.FromResult<string?>(null); };

        Assert.True(await commands.TryHandleAsync("/model", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.NotNull(offered);
        Assert.Equal("cohere/north-mini-code:free", offered[0].Value);
        Assert.Contains("current", offered[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_WithBareOpenrouter_PostsTheIdAndKeyPair()
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

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\"}");
        });
        commands.SecretPrompt = (_, _) => Task.FromResult<string?>("sk-or-test");

        Assert.True(await commands.TryHandleAsync("/key", "openrouter", "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var body = Assert.Single(posted);
        Assert.Contains("models_dev_provider_id", body, StringComparison.Ordinal);
        Assert.Contains("openrouter", body, StringComparison.Ordinal);
        Assert.Contains("models_dev_api_key", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-or-test", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_WithLiveCatalog_OffersCatalogModels()
    {
        var output = new StringWriter();
        var commands = Create(output, request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/models", StringComparison.Ordinal))
            {
                return Json("{\"status\":\"ok\",\"backend\":\"models.dev:openrouter\"," +
                    "\"catalog\":[{\"id\":\"b-model\",\"supported\":true},{\"id\":\"a-model\",\"supported\":true},{\"id\":\"old-model\",\"supported\":false}]}");
            }

            return Json("{\"status\":\"ok\",\"backend\":\"models.dev:openrouter\",\"model\":\"b-model\"}");
        });
        IReadOnlyList<ChoiceItem>? offered = null;
        commands.Picker = (list, _) => { offered = list.Items; return Task.FromResult<string?>("a-model"); };

        Assert.True(await commands.TryHandleAsync("/model", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.NotNull(offered);
        Assert.Equal("b-model", offered[0].Value);
        Assert.Contains("current", offered[0].Detail, StringComparison.Ordinal);
        Assert.Contains(offered, i => i.Value == "a-model" && i.Enabled);
        Assert.Contains(offered, i => i.Value == "old-model" && !i.Enabled);
        Assert.Contains("Switched active model to: a-model", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_WhenCatalogFails_FallsBackToConfiguredModels()
    {
        var output = new StringWriter();
        var commands = Create(output, request => request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Json("{\"status\":\"ok\",\"backend\":\"models.dev:openrouter\",\"model\":\"b-model\"}"));

        Assert.True(await commands.TryHandleAsync("/model", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.Contains("╭─ model", text, StringComparison.Ordinal);
        Assert.Contains("b-model", text, StringComparison.Ordinal);
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
    public async Task Model_OnModelsDevBackend_ShowsKeyHintInsteadOfSilentAbsence()
    {
        var output = new StringWriter();
        var commands = Create(output, _ => Json(
            "{\"status\":\"ok\",\"backend\":\"models.dev:opencode\",\"model\":\"glm-5.3-flash\"}"));

        Assert.True(await commands.TryHandleAsync("/model", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.Contains("models.dev:opencode", text, StringComparison.Ordinal);
        Assert.Contains("glm-5.3-flash", text, StringComparison.Ordinal);
        Assert.Contains("/key", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_WithoutArgs_ShowsSignInState()
    {
        var output = new StringWriter();
        var commands = Create(output, request => request.RequestUri!.AbsolutePath.EndsWith("/settings/models-dev-keys", StringComparison.Ordinal)
            ? Json("{\"status\":\"ok\",\"provider_ids\":[\"opencode\"]}")
            : Json("{\"status\":\"ok\",\"backend\":\"OpenAI\",\"openai_model\":\"gpt-4o\",\"has_openai_api_key\":true,\"has_anthropic_api_key\":false}"));

        Assert.True(await commands.TryHandleAsync("/key", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.Contains("╭─ keys", text, StringComparison.Ordinal);
        Assert.Contains("OpenAI", text, StringComparison.Ordinal);
        Assert.Contains("signed in", text, StringComparison.Ordinal);
        Assert.Contains("models.dev", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_WithProvider_PromptsOnceAndPostsTheKeyField()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return Json("{\"status\":\"success\"}");
            }

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\",\"openai_model\":\"gpt-4o\"}");
        });
        commands.SecretPrompt = (_, _) => Task.FromResult<string?>("sk-test-key");

        Assert.True(await commands.TryHandleAsync("/key", "openai", "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var body = Assert.Single(posted);
        Assert.Contains("openai_api_key", body, StringComparison.Ordinal);
        Assert.Contains("sk-test-key", body, StringComparison.Ordinal);
        var text = output.ToString();
        Assert.Contains("Signed in to OpenAI", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-test-key", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_WithModelsDevId_PostsTheIdAndKeyPair()
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

            return Json("{\"status\":\"ok\",\"backend\":\"models.dev:opencode\",\"model\":\"glm-5.3-flash\"}");
        });
        commands.SecretPrompt = (_, _) => Task.FromResult<string?>("sk-test-key");

        Assert.True(await commands.TryHandleAsync("/key", "models.dev:opencode", "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var body = Assert.Single(posted);
        Assert.Contains("models_dev_provider_id", body, StringComparison.Ordinal);
        Assert.Contains("models_dev_api_key", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-test-key", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_WhenCancelled_SavesNothing()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add("POST");
                return Json("{\"status\":\"ok\"}");
            }

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\"}");
        });
        commands.SecretPrompt = (_, _) => Task.FromResult<string?>(null);

        Assert.True(await commands.TryHandleAsync("/key", "openai", "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.Empty(posted);
        Assert.Contains("no key entered", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_WithEmptyKey_SavesNothing()
    {
        var posted = new List<string>();
        var commands = Create(new StringWriter(), request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add("POST");
                return Json("{\"status\":\"ok\"}");
            }

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\"}");
        });
        commands.SecretPrompt = (_, _) => Task.FromResult<string?>("   ");

        Assert.True(await commands.TryHandleAsync("/key", "openai", "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.Empty(posted);
    }

    [Fact]
    public async Task Key_PrefersTheMaskedPanelOverTheBarePrompt()
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

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\"}");
        });
        var prompts = 0;
        commands.SecretPrompt = (_, _) => { prompts++; return Task.FromResult<string?>("sk-bare"); };
        commands.SecretPanelPrompt = (_, _) => Task.FromResult<string?>("sk-panel-key");

        Assert.True(await commands.TryHandleAsync("/key", "openrouter", "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var body = Assert.Single(posted);
        Assert.Contains("sk-panel-key", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-bare", body, StringComparison.Ordinal);
        Assert.Equal(0, prompts);
        // The confirmation names the provider the way the user typed it; the canonical
        // models.dev: form travels in the payload, which the assertions above cover.
        Assert.Contains("Signed in to openrouter", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_PanelCancel_SavesNothing()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add("POST");
                return Json("{\"status\":\"ok\"}");
            }

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\"}");
        });
        commands.SecretPanelPrompt = (_, _) => Task.FromResult<string?>(null);

        Assert.True(await commands.TryHandleAsync("/key", "openrouter", "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.Empty(posted);
        Assert.Contains("no key entered", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_WithPaddedKey_TrimsBeforeSaving()
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

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\"}");
        });
        commands.SecretPrompt = (_, _) => Task.FromResult<string?>("  sk-test-key \r\n");

        Assert.True(await commands.TryHandleAsync("/key", "openai", "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var body = Assert.Single(posted);
        Assert.Contains("sk-test-key", body, StringComparison.Ordinal);
        Assert.DoesNotContain("  sk-test-key", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_WithUnknownProvider_SavesNothing()
    {
        var posted = new List<string>();
        var commands = Create(new StringWriter(), request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add("POST");
                return Json("{\"status\":\"ok\"}");
            }

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\"}");
        });
        var prompts = 0;
        commands.SecretPrompt = (_, _) =>
        {
            prompts++;
            return Task.FromResult<string?>("sk-test-key");
        };

        Assert.True(await commands.TryHandleAsync("/key", "nope", "sess_1", "standard", "D:\\LLM", CancellationToken.None));
        Assert.True(await commands.TryHandleAsync("/key", "models.dev:BAD!ID", "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.Empty(posted);
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task KeyRemove_PostsTheBackendChoice()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return Json("{\"status\":\"success\",\"message\":\"Signed out.\"}");
            }

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\"}");
        });

        Assert.True(await commands.TryHandleAsync("/key", "remove openai", "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var body = Assert.Single(posted);
        Assert.Contains("backend_choice", body, StringComparison.Ordinal);
        Assert.Contains("Signed out.", output.ToString(), StringComparison.Ordinal);
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
