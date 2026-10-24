using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private static AgentSlashCommands Create(
        StringWriter output,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond,
        string settingsJson = "{\"status\":\"ok\",\"backend\":\"OpenAI\",\"openai_model\":\"gpt-4o\",\"has_openai_api_key\":true,\"anthropic_model\":\"claude-sonnet-4-5\"}")
    {
        var client = new SettingsApiClient(
            Session(),
            new HttpClient(new FakeHandler(respond)));
        return new AgentSlashCommands(client, output, new StringWriter()) { PanelWidth = 80 };
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static AgentSlashCommands CreateWithSessions(
        StringWriter output,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var client = new SettingsApiClient(
            Session(),
            new HttpClient(new FakeHandler(respond)));
        var sessions = new SessionApiClient(
            Session(),
            new HttpClient(new FakeHandler(respond)));
        return new AgentSlashCommands(client, sessions, output, new StringWriter()) { PanelWidth = 80 };
    }

    [Fact]
    public async Task Help_RendersTheCommandPanel()
    {
        var output = new StringWriter();
        var commands = Create(output);

        Assert.True(await commands.TryHandleAsync("/help", null, null, "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.Contains("╭─ help", text, StringComparison.Ordinal);
        Assert.Contains("/model", text, StringComparison.Ordinal);
        Assert.Contains("/exit", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/quit", text, StringComparison.Ordinal);
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
    public async Task Status_StripsTerminalControlsFromSettingsValues()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.None);
        var output = new StringWriter();
        var commands = Create(
            output,
            settingsJson: "{\"status\":\"ok\",\"backend\":\"OpenAI\",\"openai_model\":\"gpt\\u0007-4o\",\"has_openai_api_key\":true}");

        Assert.True(await commands.TryHandleAsync("/status", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\u0007', text);
        Assert.Contains("gpt", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/activity")]
    [InlineData("/todos")]
    [InlineData("/changes")]
    [InlineData("/files")]
    [InlineData("/tree")]
    [InlineData("/stats")]
    [InlineData("/tokens")]
    [InlineData("/export")]
    [InlineData("/rewind")]
    [InlineData("/history")]
    public async Task RemovedCommands_AreAbsentFromHelpAndDoNotCallTheHost(string command)
    {
        var output = new StringWriter();
        var requests = 0;
        var commands = Create(output, _ => { requests++; return Json("{}"); });

        Assert.False(await commands.TryHandleAsync(command, null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));
        Assert.Equal(0, requests);
        Assert.Empty(ChatCommandCatalog.FindMatches(command));
        Assert.DoesNotContain(command, string.Join('\n', TuiSurfaces.Help(120)), StringComparison.Ordinal);
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
        var commands = Create(output, async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(await request.Content!.ReadAsStringAsync());
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
        var commands = Create(output, async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(await request.Content!.ReadAsStringAsync());
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

    /// <summary>
    /// I1's no-op guard: the TG picker leaves no scrollback behind it, so a cancelled bare
    /// `/backend` must still leave the option panel in the transcript.
    /// </summary>
    [Fact]
    public async Task Backend_PickerCancel_StillPrintsThePanel()
    {
        var output = new StringWriter();
        var commands = Create(output);
        commands.ReprintPanelOnPickerCancel = true;
        commands.Picker = (_, _) => Task.FromResult<string?>(null);

        Assert.True(await commands.TryHandleAsync("/backend", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.Contains("╭─ backend", text, StringComparison.Ordinal);
        Assert.Contains("OpenAI", text, StringComparison.Ordinal);
        Assert.Contains("/backend <name>", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backend_WithAnArgument_SwitchesDirectly()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(await request.Content!.ReadAsStringAsync());
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
        var commands = Create(output, async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(await request.Content!.ReadAsStringAsync());
                return Json("{\"status\":\"ok\"}");
            }

            return Json("{\"status\":\"ok\",\"backend\":\"OpenAI\"}");
        });

        await commands.TryHandleAsync("/backend", "openrouter", "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        Assert.Contains("models.dev:openrouter", posted.Single(), StringComparison.Ordinal);
        Assert.Contains("Switched backend to: models.dev:openrouter", output.ToString(), StringComparison.Ordinal);
    }

    private const string ReasoningSettings =
        """{"status":"ok","backend":"openai","openai_model":"gpt-5","has_openai_api_key":true}""";

    private const string ReasoningCatalog =
        """
        {"status":"ok","backend":"openai","identity":"ident-42","selected_model":"gpt-5","selected_effort":"default",
         "catalog":[{"id":"gpt-5","supported":true,"reasoning":{"state":"levels","options":["low","medium","high"]}}],
         "models":[]}
        """;

    private static HttpResponseMessage Router(
        HttpRequestMessage request,
        string catalog,
        List<string> posted,
        string? modelsDev = null)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post)
        {
            posted.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Json("{\"status\":\"ok\"}");
        }

        if (path.EndsWith("/models-dev", StringComparison.Ordinal))
        {
            return modelsDev is null ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(modelsDev);
        }

        return path.EndsWith("/models", StringComparison.Ordinal) ? Json(catalog) : Json(ReasoningSettings);
    }

    [Fact]
    public async Task Reasoning_WithoutAPicker_PrintsTheLevelsTheModelOffers()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request => Router(request, ReasoningCatalog, posted));

        Assert.True(await commands.TryHandleAsync("/reasoning", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.Contains("╭─ reasoning", text, StringComparison.Ordinal);
        Assert.Contains("gpt-5", text, StringComparison.Ordinal);
        Assert.Contains("low", text, StringComparison.Ordinal);
        Assert.Contains("high", text, StringComparison.Ordinal);
        Assert.Empty(posted);
    }

    [Fact]
    public async Task Reasoning_ForAModelWithNoLevels_ExplainsInsteadOfOpeningAPicker()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        const string noLevels =
            """
            {"status":"ok","backend":"models.dev:openrouter","identity":"i","selected_model":"poolside/x:free","selected_effort":"default",
             "catalog":[{"id":"poolside/x:free","reasoning":{"state":"unknown","options":[]}}],"models":[]}
            """;
        var commands = Create(output, request => Router(request, noLevels, posted));

        await commands.TryHandleAsync("/reasoning", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        var text = output.ToString();
        Assert.Contains("offers no reasoning effort", text, StringComparison.Ordinal);
        Assert.Contains("poolside/x:free", text, StringComparison.Ordinal);
        Assert.DoesNotContain("╭─ reasoning", text, StringComparison.Ordinal);
        Assert.Empty(posted);
    }

    [Fact]
    public async Task Reasoning_WithAnArgument_PostsExactlyTheThreeBindingFields()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request => Router(request, ReasoningCatalog, posted));

        await commands.TryHandleAsync("/reasoning", "high", "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        var body = JsonNode.Parse(posted.Single())!.AsObject();
        Assert.Equal(3, body.Count);
        Assert.Equal("high", body["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("gpt-5", body["reasoning_model"]!.GetValue<string>());
        Assert.Equal("ident-42", body["reasoning_identity"]!.GetValue<string>());
        Assert.Contains("reasoning effort: high", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reasoning_AcceptsDefaultEvenWhenTheCatalogPublishesNoLevels()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        const string noLevels =
            """
            {"status":"ok","backend":"models.dev:openrouter","identity":"i","selected_model":"poolside/x:free","selected_effort":"default",
             "catalog":[{"id":"poolside/x:free","reasoning":{"state":"unknown","options":[]}}],"models":[]}
            """;
        var commands = Create(output, request => Router(request, noLevels, posted));

        await commands.TryHandleAsync("/reasoning", "default", "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        Assert.Contains("\"reasoning_effort\":\"default\"", posted.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reasoning_RejectsAnEffortTheModelNeverOffered()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request => Router(request, ReasoningCatalog, posted));

        await commands.TryHandleAsync("/reasoning", "extreme", "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        Assert.Empty(posted);
    }

    [Fact]
    public async Task Reasoning_NeverOpensThePickerForANonInteractiveHost()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, request => Router(request, ReasoningCatalog, posted));
        var opened = new List<ChoiceList>();
        commands.Picker = (list, _) =>
        {
            opened.Add(list);
            return Task.FromResult<string?>("high");
        };

        // A test host has no console, so the bare form must fall back to the panel and
        // leave the configuration alone rather than picking something.
        await commands.TryHandleAsync("/reasoning", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        Assert.Empty(opened);
        Assert.Empty(posted);
        Assert.Contains("╭─ reasoning", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reasoning_WhenTheCatalogCallFails_ReportsBoundedReasoningUnavailable()
    {
        var output = new StringWriter();
        var commands = Create(output, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await commands.TryHandleAsync("/reasoning", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task Backend_SaysSoWhenTheProviderCatalogCannotBeFetched()
    {
        var output = new StringWriter();
        var commands = Create(output, request => Router(request, ReasoningCatalog, []));

        await commands.TryHandleAsync("/backend", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        var text = output.ToString();
        Assert.Contains("models.dev list unavailable", text, StringComparison.Ordinal);
        Assert.Contains("known providers only", text, StringComparison.Ordinal);
        Assert.Contains("models.dev:kilo", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backend_AnnouncesTheLiveProviderCount()
    {
        var output = new StringWriter();
        var commands = Create(output, request => Router(
            request,
            ReasoningCatalog,
            [],
            """{"status":"ok","providers":[{"id":"openrouter","name":"OpenRouter"},{"id":"groq","name":"Groq"}]}"""));

        await commands.TryHandleAsync("/backend", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None);

        var text = output.ToString();
        Assert.Contains("2 providers from models.dev", text, StringComparison.Ordinal);
        Assert.Contains("models.dev:groq", text, StringComparison.Ordinal);
        Assert.DoesNotContain("models.dev list unavailable", text, StringComparison.Ordinal);
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
        var commands = Create(output, async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(await request.Content!.ReadAsStringAsync());
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
        var commands = Create(output, async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/models", StringComparison.Ordinal))
            {
                return Json("{\"status\":\"ok\",\"backend\":\"models.dev:openrouter\"," +
                    "\"catalog\":[{\"id\":\"b-model\",\"supported\":true},{\"id\":\"a-model\",\"supported\":true},{\"id\":\"old-model\",\"supported\":false}]}");
            }

            return Json("{\"status\":\"ok\",\"backend\":\"models.dev:openrouter\",\"model\":\"b-model\"}");
        });
        ChoiceList? offered = null;
        commands.Picker = (list, _) => { offered = list; return Task.FromResult<string?>("a-model"); };

        Assert.True(await commands.TryHandleAsync("/model", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.NotNull(offered);
        Assert.Equal("b-model", offered!.Items[0].Value);
        Assert.Contains("current", offered.Items[0].Detail, StringComparison.Ordinal);
        Assert.Contains(offered.Items, i => i.Value == "a-model" && i.Enabled);
        Assert.Contains(offered.Items, i => i.Value == "old-model" && !i.Enabled);
        // The footer names the backend and the row count, per the README promise.
        Assert.Contains("models.dev:openrouter · 3 rows", string.Join("\n", offered.Render(80)), StringComparison.Ordinal);
        Assert.Contains("Switched active model to: a-model", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_WhenCatalogFails_FallsBackToConfiguredModels()
    {
        var output = new StringWriter();
        var commands = Create(output, async request => request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Json("{\"status\":\"ok\",\"backend\":\"models.dev:openrouter\",\"model\":\"b-model\"}"));

        Assert.True(await commands.TryHandleAsync("/model", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.Contains("╭─ model", text, StringComparison.Ordinal);
        Assert.Contains("b-model", text, StringComparison.Ordinal);
        // The silent fallback was the bug: the operator must see why the live catalog is missing.
        Assert.Contains("catalog unavailable (HTTP 500) - showing configured models", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_WithAnArgument_SwitchesDirectly()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(await request.Content!.ReadAsStringAsync());
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
        var commands = Create(output, async request => request.RequestUri!.AbsolutePath.EndsWith("/settings/models-dev-keys", StringComparison.Ordinal)
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
        var commands = Create(output, async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(await request.Content!.ReadAsStringAsync());
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
        var commands = Create(output, async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(await request.Content!.ReadAsStringAsync());
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
        var commands = Create(output, async request =>
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
        var commands = Create(output, async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(await request.Content!.ReadAsStringAsync());
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
        var commands = Create(output, async request =>
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
        var commands = Create(output, async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(await request.Content!.ReadAsStringAsync());
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
        var commands = Create(output, async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(await request.Content!.ReadAsStringAsync());
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
    public async Task Delete_RefusesTheCurrentSession()
    {
        var output = new StringWriter();
        var deletes = new List<string>();
        var commands = CreateWithSessions(output, request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                deletes.Add(request.RequestUri!.AbsolutePath);
            }

            return Json("{\"status\":\"ok\"}");
        });
        commands.Picker = (_, _) => Task.FromResult<string?>("delete");

        // The bare form targets the current session, and so does the explicit id.
        Assert.True(await commands.TryHandleAsync("/delete", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));
        Assert.True(await commands.TryHandleAsync("/delete", "sess_1", "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.Empty(deletes);
        Assert.Contains("current session", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_ConfirmsBeforeDeletingAnotherSession()
    {
        var output = new StringWriter();
        var deletes = new List<string>();
        var commands = CreateWithSessions(output, request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                deletes.Add(request.RequestUri!.AbsolutePath);
            }

            return Json("{\"status\":\"ok\"}");
        });
        ChoiceList? offered = null;
        commands.Picker = (list, _) =>
        {
            offered = list;
            return Task.FromResult<string?>("delete");
        };

        Assert.True(await commands.TryHandleAsync("/delete", "sess_2", "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.NotNull(offered);
        // Enter or Escape on the untouched confirm list must keep the session.
        Assert.Equal("keep", offered!.Items[offered.Selected].Value);
        Assert.Equal("/sessions/sess_2", Assert.Single(deletes));
        Assert.Contains("Deleted session sess_2", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_DeclinedConfirm_DeletesNothing()
    {
        var output = new StringWriter();
        var deletes = new List<string>();
        var commands = CreateWithSessions(output, request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                deletes.Add(request.RequestUri!.AbsolutePath);
            }

            return Json("{\"status\":\"ok\"}");
        });
        commands.Picker = (_, _) => Task.FromResult<string?>(null);

        Assert.True(await commands.TryHandleAsync("/delete", "sess_2", "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.Empty(deletes);
        Assert.Contains("not deleted", output.ToString(), StringComparison.Ordinal);
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
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _func;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = req => Task.FromResult(func(req));
        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _func(request);
    }
}
