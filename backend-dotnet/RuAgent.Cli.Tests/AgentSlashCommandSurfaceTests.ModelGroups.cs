using System.Net;
using System.Text.Json;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// The grouped <c>/model</c> surface across signed-in providers, and the model step that
/// <c>/provider</c> now continues into so a switch never leaves the previous provider's model
/// pinned in settings.
/// </summary>
public sealed partial class AgentSlashCommandSurfaceTests
{
    private static HttpResponseMessage Catalog(params (string Id, bool Supported)[] models)
    {
        var entries = models.Select(model =>
            $"{{\"id\":\"{model.Id}\",\"supported\":{(model.Supported ? "true" : "false")}}}");
        return Json($"{{\"status\":\"ok\",\"catalog\":[{string.Join(',', entries)}]}}");
    }

    private static string CombinedSettings(string backend, string model) =>
        $"{{\"status\":\"ok\",\"backend\":\"{backend}\",\"model\":\"{model}\",\"has_openai_api_key\":false,\"has_anthropic_api_key\":false}}";

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> TwoProviderHost(
        List<string> posted,
        params string[] modelsDevKeys)
    {
        return async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(await request.Content!.ReadAsStringAsync());
                return Json("{\"status\":\"success\"}");
            }

            switch (path)
            {
                case "/settings":
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            CombinedSettings("models.dev:kilo", "kilo-model"),
                            System.Text.Encoding.UTF8,
                            "application/json"),
                    };
                case "/settings/models-dev-keys":
                    return Json(
                        "{\"status\":\"ok\",\"provider_ids\":["
                        + string.Join(',', modelsDevKeys.Select(id => $"\"{id}\""))
                        + "]}");
                case "/models-dev":
                    return Json(
                        "{\"status\":\"ok\",\"providers\":[{\"id\":\"kilo\",\"name\":\"Kilo Gateway\"},"
                        + "{\"id\":\"opencode-go\",\"name\":\"OpenCode Go\"}]}");
                case "/models":
                    var backend = Uri.UnescapeDataString(request.RequestUri.Query);
                    if (backend.Contains("models.dev:kilo", StringComparison.Ordinal))
                    {
                        return Catalog(("kilo-model", true), ("kilo-free:free", true));
                    }

                    if (backend.Contains("models.dev:opencode-go", StringComparison.Ordinal))
                    {
                        return Catalog(("go-flash", true), ("go-pro", true));
                    }

                    return Catalog(("unknown-model", true));
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };
    }

    [Fact]
    public async Task Model_WithTwoSignedInProviders_GroupsBothCatalogsAndSavesProviderAndModel()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, TwoProviderHost(posted, "kilo", "opencode-go"));
        ChoiceList? offered = null;
        commands.Picker = (list, _) =>
        {
            offered = list;
            var row = list.Items.Single(item => item.Label == "go-flash");
            return Task.FromResult<string?>(row.Value);
        };

        Assert.True(await commands.TryHandleAsync("/model", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.NotNull(offered);
        Assert.Contains(offered!.Items, item => item.Label == "Kilo Gateway" && !item.Enabled);
        Assert.Contains(offered.Items, item => item.Label == "OpenCode Go" && !item.Enabled);
        Assert.Contains(offered.Items, item => item.Label == "kilo-model" && item.Enabled);
        Assert.Contains(offered.Items, item => item.Label == "go-pro" && item.Enabled);
        // Minimal chrome: the provider headings say everything, so no footer caption or ratio.
        Assert.Null(offered.Footer);
        Assert.True(offered.HideFooter);
        Assert.False(offered.ShowCounter);

        var saved = JsonDocument.Parse(Assert.Single(posted)).RootElement;
        Assert.Equal("models.dev:opencode-go", saved.GetProperty("backend_choice").GetString());
        Assert.Equal("go-flash", saved.GetProperty("selected_model").GetString());
        Assert.Contains("✓ model: go-flash (OpenCode Go)", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_WithTwoSignedInProviders_WithoutAPickerRendersBothGroupPanels()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, TwoProviderHost(posted, "kilo", "opencode-go"));

        Assert.True(await commands.TryHandleAsync("/model", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        var text = output.ToString();
        Assert.Contains("╭─ model", text, StringComparison.Ordinal);
        Assert.Contains("Kilo Gateway", text, StringComparison.Ordinal);
        Assert.Contains("OpenCode Go", text, StringComparison.Ordinal);
        Assert.Contains("kilo-model", text, StringComparison.Ordinal);
        Assert.Contains("go-pro", text, StringComparison.Ordinal);
        Assert.Empty(posted);
    }

    [Fact]
    public async Task Model_CancelledGroupedPicker_PrintsNothingAndSavesNothing()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var commands = Create(output, TwoProviderHost(posted, "kilo", "opencode-go"));
        commands.Picker = (_, _) => Task.FromResult<string?>(null);

        Assert.True(await commands.TryHandleAsync("/model", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.Equal(string.Empty, output.ToString());
        Assert.Empty(posted);
    }

    [Fact]
    public async Task Model_WithoutAnActiveBackend_KeepsTheSingleCatalogFallback()
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

            return request.RequestUri!.AbsolutePath switch
            {
                "/settings" => Json("{\"status\":\"ok\",\"backend\":\"\",\"model\":\"\"}"),
                "/settings/models-dev-keys" => Json("{\"status\":\"ok\",\"provider_ids\":[\"kilo\"]}"),
                "/models-dev" => Json(
                    "{\"status\":\"ok\",\"providers\":[{\"id\":\"kilo\",\"name\":\"Kilo Gateway\"}]}"),
                "/models" => Catalog(("default-model", true), ("other-model", true)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        ChoiceList? offered = null;
        commands.Picker = (list, _) =>
        {
            offered = list;
            return Task.FromResult<string?>(null);
        };

        Assert.True(await commands.TryHandleAsync("/model", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.NotNull(offered);
        Assert.DoesNotContain(offered!.Items, item => !item.Enabled);
        Assert.Equal(["default-model", "other-model"], offered.Items.Select(item => item.Label));
        Assert.Empty(posted);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Provider_AfterSwitch_OffersTheNewCatalogAndSavesBoth()
    {
        var output = new StringWriter();
        var posted = new List<string>();
        var paths = new List<string>();
        var commands = Create(output, async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                posted.Add(await request.Content!.ReadAsStringAsync());
                paths.Add(path);
                return Json("{\"status\":\"success\"}");
            }

            return path switch
            {
                "/settings" => Json(CombinedSettings("models.dev:kilo", "kilo-model")),
                "/settings/models-dev-keys" => Json("{\"status\":\"ok\",\"provider_ids\":[\"opencode-go\"]}"),
                "/models-dev" => Json(
                    "{\"status\":\"ok\",\"providers\":[{\"id\":\"opencode-go\",\"name\":\"OpenCode Go\"}]}"),
                "/models" => Catalog(("go-flash", true), ("go-pro", true)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        commands.SecretPanelPrompt = (_, _) => Task.FromResult<string?>("synthetic-key");
        var picks = new Queue<string?>(["models.dev:opencode-go", "go-pro"]);
        var pickerCalls = 0;
        ChoiceList? providerList = null;
        commands.Picker = (list, _) =>
        {
            pickerCalls++;
            providerList ??= list;
            return Task.FromResult(picks.Count > 0 ? picks.Dequeue() : null);
        };

        Assert.True(await commands.TryHandleAsync("/provider", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.Equal(2, pickerCalls);
        Assert.NotNull(providerList);
        // Minimal provider panel: short title, friendly names, no footer caption or ratio.
        Assert.Equal("provider", providerList!.Title);
        Assert.True(providerList.HideFooter);
        Assert.False(providerList.ShowCounter);
        Assert.Null(providerList.Footer);
        Assert.Contains(providerList.Items, item => item.Label == "OpenCode Go" && item.Value == "models.dev:opencode-go");
        var signedIn = Assert.Single(providerList.Items, item => item.Value == "models.dev:opencode-go");
        Assert.Equal("✓", signedIn.Detail);
        Assert.Equal(TuiToken.Ok, signedIn.DetailTone);
        Assert.DoesNotContain(providerList.Items, item => item.Detail.Contains("key ok", StringComparison.Ordinal));
        Assert.DoesNotContain(providerList.Items, item => item.Detail.Contains("no key", StringComparison.Ordinal));
        Assert.DoesNotContain(providerList.Items, item => item.Label.StartsWith("models.dev:", StringComparison.Ordinal));
        Assert.DoesNotContain(providerList.Items, item => item.Detail.Contains("Models.dev:", StringComparison.Ordinal));
        Assert.Equal(["/settings/provider/key", "/settings", "/settings"], paths);
        var saved = JsonDocument.Parse(posted[^1]).RootElement;
        Assert.Equal("models.dev:opencode-go", saved.GetProperty("backend_choice").GetString());
        Assert.Equal("go-pro", saved.GetProperty("selected_model").GetString());
        var text = output.ToString();
        Assert.Contains("Provider selected: opencode-go", text, StringComparison.Ordinal);
        Assert.Contains("✓ model: go-pro (opencode-go)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_AfterSwitch_AutoSelectsTheOnlyModel()
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

            return request.RequestUri!.AbsolutePath switch
            {
                "/settings" => Json(CombinedSettings("models.dev:kilo", "kilo-model")),
                "/settings/models-dev-keys" => Json("{\"status\":\"ok\",\"provider_ids\":[\"opencode-go\"]}"),
                "/models-dev" => Json(
                    "{\"status\":\"ok\",\"providers\":[{\"id\":\"opencode-go\",\"name\":\"OpenCode Go\"}]}"),
                "/models" => Catalog(("only-model", true)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        commands.SecretPanelPrompt = (_, _) => Task.FromResult<string?>("synthetic-key");
        var pickerCalls = 0;
        commands.Picker = (_, _) =>
        {
            pickerCalls++;
            return Task.FromResult<string?>("models.dev:opencode-go");
        };

        Assert.True(await commands.TryHandleAsync("/provider", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.Equal(1, pickerCalls);
        var saved = JsonDocument.Parse(posted[^1]).RootElement;
        Assert.Equal("models.dev:opencode-go", saved.GetProperty("backend_choice").GetString());
        Assert.Equal("only-model", saved.GetProperty("selected_model").GetString());
        Assert.Contains("✓ model: only-model (opencode-go)", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_WhenTheNewCatalogIsUnavailable_StillSwitchesAndPointsAtModel()
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

            return request.RequestUri!.AbsolutePath switch
            {
                "/settings" => Json(CombinedSettings("models.dev:kilo", "kilo-model")),
                "/settings/models-dev-keys" => Json("{\"status\":\"ok\",\"provider_ids\":[\"opencode-go\"]}"),
                "/models-dev" => Json(
                    "{\"status\":\"ok\",\"providers\":[{\"id\":\"opencode-go\",\"name\":\"OpenCode Go\"}]}"),
                "/models" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        commands.SecretPanelPrompt = (_, _) => Task.FromResult<string?>("synthetic-key");
        commands.Picker = (_, _) => Task.FromResult<string?>("models.dev:opencode-go");

        Assert.True(await commands.TryHandleAsync("/provider", null, "sess_1", "standard", "D:\\LLM", CancellationToken.None));

        Assert.Equal(2, posted.Count);
        Assert.Contains("Provider selected: opencode-go", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("run /model", output.ToString(), StringComparison.Ordinal);
    }
}
