using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ProviderSetupTests
{
    internal sealed class Host : HttpMessageHandler
    {
        internal bool HasKey, FailSave;
        internal List<JsonElement> Posts = [];
        internal List<string> PostPaths = [];
        /// <summary>What the checked key route answers.</summary>
        internal string KeyVerdict = """{"status":"success","message":"Settings saved."}""";
        internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); Posts.Add(body.RootElement.Clone());
                PostPaths.Add(request.RequestUri!.AbsolutePath);
                if (FailSave) throw new HttpRequestException("secret-sentinel synthetic transport error");
                return Json(request.RequestUri!.AbsolutePath == "/settings/provider/key" ? KeyVerdict : """{"status":"success"}""");
            }
            return request.RequestUri!.AbsolutePath switch
            {
                "/models-dev" => Json("""{"status":"ok","providers":[{"id":"openrouter","name":"OpenRouter"}]}"""),
                "/settings/models-dev-keys" => Json("""{"status":"ok","provider_ids":[]}"""),
                "/settings" => Json(JsonSerializer.Serialize(new { status = "ok", backend = "OpenAI", has_openai_api_key = HasKey })),
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
            };
        }
        internal AgentSlashCommands Commands(HttpClient http, StringWriter output, StringWriter errors) =>
            new(new SettingsApiClient(ChatTestSession.Value, http), output, errors) { PanelWidth = 100 };
    }

    [Fact]
    public async Task Picker_then_secret_checks_the_key_before_switching_provider()
    {
        using var host = new Host(); using var http = new HttpClient(host);
        var output = new StringWriter(); var errors = new StringWriter(); var commands = host.Commands(http, output, errors);
        var steps = new List<string>();
        commands.Picker = (list, _) => { steps.Add("provider"); Assert.Empty(host.Posts); return Task.FromResult<string?>("models.dev:openrouter"); };
        commands.SecretPanelPrompt = (title, _) => { steps.Add("key"); Assert.Contains("openrouter", title); Assert.Empty(host.Posts); return Task.FromResult<string?>(" secret-sentinel "); };
        await commands.TryHandleAsync("/provider", null, default);
        Assert.Equal(["provider", "key"], steps);
        // The key goes to the checked route alone; the selection follows only once it is stored.
        Assert.Equal(["/settings/provider/key", "/settings"], host.PostPaths);
        var key = host.Posts[0];
        Assert.Equal(2, key.EnumerateObject().Count());
        Assert.Equal("models.dev:openrouter", key.GetProperty("models_dev_provider_id").GetString());
        Assert.Equal("secret-sentinel", key.GetProperty("models_dev_api_key").GetString());
        var selection = Assert.Single(host.Posts[1].EnumerateObject());
        Assert.Equal("backend", selection.Name);
        Assert.Equal("models.dev:openrouter", selection.Value.GetString());
        Assert.DoesNotContain("secret-sentinel", output.ToString() + errors.ToString());
    }

    [Theory]
    [InlineData("provider_key_rejected", "refused this key")]
    [InlineData("provider_key_unverified", "could not reach")]
    [InlineData("provider_key_malformed", "control character")]
    public async Task Refused_key_switches_nothing_and_says_why(string code, string reason)
    {
        using var host = new Host { KeyVerdict = $$"""{"status":"error","message":"Key not saved.","code":"{{code}}"}""" };
        using var http = new HttpClient(host);
        var output = new StringWriter(); var errors = new StringWriter(); var commands = host.Commands(http, output, errors);
        commands.SecretPanelPrompt = (_, _) => Task.FromResult<string?>("secret-sentinel");
        await commands.TryHandleAsync("/provider", "anthropic", default);
        Assert.Equal(["/settings/provider/key"], host.PostPaths);
        Assert.Contains(code, errors.ToString());
        Assert.Contains(reason, errors.ToString());
        Assert.DoesNotContain("Provider selected", output.ToString());
        Assert.DoesNotContain("secret-sentinel", output.ToString() + errors.ToString());
    }

    [Fact]
    public async Task Unchecked_key_is_saved_and_says_the_first_request_will_check_it()
    {
        using var host = new Host { KeyVerdict = """{"status":"success","message":"Key saved.","code":"provider_key_unchecked"}""" };
        using var http = new HttpClient(host);
        var output = new StringWriter(); var commands = host.Commands(http, output, new());
        commands.SecretPanelPrompt = (_, _) => Task.FromResult<string?>("synthetic-key");
        await commands.TryHandleAsync("/provider", "models.dev:openrouter", default);
        Assert.Equal(["/settings/provider/key", "/settings"], host.PostPaths);
        Assert.Contains("the first request will", output.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Cancelled_or_missing_required_key_does_not_switch_provider(string? key)
    {
        using var host = new Host(); using var http = new HttpClient(host);
        var commands = host.Commands(http, new(), new());
        commands.SecretPanelPrompt = (_, _) => Task.FromResult(key);
        await commands.TryHandleAsync("/provider", "anthropic", default);
        Assert.Empty(host.Posts);
    }

    [Fact]
    public async Task Cancelled_provider_picker_never_requests_key()
    {
        using var host = new Host(); using var http = new HttpClient(host);
        var commands = host.Commands(http, new(), new());
        commands.Picker = (_, _) => Task.FromResult<string?>(null);
        commands.SecretPanelPrompt = (_, _) => throw new InvalidOperationException("must not prompt");
        await commands.TryHandleAsync("/provider", null, default); Assert.Empty(host.Posts);
    }

    [Theory]
    [InlineData("openai", "openai", "openai_api_key")]
    [InlineData("anthropic", "anthropic", "anthropic_api_key")]
    public async Task Fixed_provider_payload_uses_shared_credential_mapping(string choice, string backend, string field)
    {
        using var host = new Host(); using var http = new HttpClient(host);
        var commands = host.Commands(http, new(), new()); commands.SecretPanelPrompt = (_, _) => Task.FromResult<string?>("synthetic-key");
        await commands.TryHandleAsync("/provider", choice, default);
        Assert.Equal(["/settings/provider/key", "/settings"], host.PostPaths);
        var key = Assert.Single(host.Posts[0].EnumerateObject());
        Assert.Equal(field, key.Name); Assert.Equal("synthetic-key", key.Value.GetString());
        var selection = Assert.Single(host.Posts[1].EnumerateObject());
        Assert.Equal(backend, selection.Value.GetString());
    }

    [Fact]
    public async Task Custom_endpoint_key_is_checked_before_the_selection_too()
    {
        using var host = new Host(); using var http = new HttpClient(host);
        var commands = host.Commands(http, new(), new()); commands.SecretPanelPrompt = (_, _) => Task.FromResult<string?>("synthetic-key");
        await commands.TryHandleAsync("/provider", "custom", default);
        Assert.Equal(["/settings/provider/key", "/settings"], host.PostPaths);
        var key = Assert.Single(host.Posts[0].EnumerateObject());
        Assert.Equal("custom_api_key", key.Name); Assert.Equal("synthetic-key", key.Value.GetString());
        Assert.Equal("custom", Assert.Single(host.Posts[1].EnumerateObject()).Value.GetString());
    }

    [Fact]
    public async Task A_keyless_custom_endpoint_selects_without_any_key_request()
    {
        using var host = new Host(); using var http = new HttpClient(host);
        var commands = host.Commands(http, new(), new()); commands.SecretPanelPrompt = (_, _) => Task.FromResult<string?>("");
        await commands.TryHandleAsync("/provider", "custom", default);
        Assert.Equal(["/settings"], host.PostPaths);
    }

    [Fact]
    public async Task Empty_entry_keeps_known_saved_key_without_resending_it()
    {
        using var host = new Host { HasKey = true }; using var http = new HttpClient(host);
        var commands = host.Commands(http, new(), new());
        commands.SecretPanelPrompt = (title, _) => { Assert.Contains("Enter keeps saved key", title); return Task.FromResult<string?>(""); };
        await commands.TryHandleAsync("/provider", "openai", default);
        Assert.Single(Assert.Single(host.Posts).EnumerateObject());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Json_or_missing_secret_prompt_does_not_mutate(bool json)
    {
        using var host = new Host(); using var http = new HttpClient(host);
        var errors = new StringWriter(); var commands = host.Commands(http, new(), errors); commands.Json = json;
        commands.SecretPanelPrompt = json ? (_, _) => throw new InvalidOperationException("must not prompt") : null;
        await commands.TryHandleAsync("/provider", "openai", default);
        Assert.Empty(host.Posts); Assert.Contains("interactive_required", errors.ToString());
    }

    [Fact]
    public async Task Failed_save_never_retries_or_echoes_secret_from_exception()
    {
        using var host = new Host { FailSave = true }; using var http = new HttpClient(host);
        var output = new StringWriter(); var errors = new StringWriter(); var commands = host.Commands(http, output, errors);
        commands.SecretPanelPrompt = (_, _) => Task.FromResult<string?>("secret-sentinel");
        await commands.TryHandleAsync("/provider", "openai", default);
        Assert.Single(host.Posts); Assert.Contains("key_save_failed", errors.ToString());
        Assert.DoesNotContain("secret-sentinel", output.ToString() + errors.ToString());
    }

    [Fact]
    public void Provider_repair_preserves_pending_attachment_ownership()
    {
        using var state = new ChatSubmissionState();
        state.Add(new RuAgent.Cli.Attachments.CliTextAttachment("draft.py", "print('draft')", 14));
        Assert.True(ChatSubmissionSnapshot.IsPausedControlCommand("/provider"));
        var snapshot = state.CreateSnapshot("/provider", "s", @"C:\workspace", "standard", "b", "m", false);
        Assert.NotNull(snapshot); Assert.Empty(snapshot.Images); Assert.Empty(snapshot.Texts);
        state.Admit(snapshot); Assert.Single(state.Texts);
    }

    [Fact]
    public async Task Unknown_picker_value_is_refused_before_secret_prompt()
    {
        using var host = new Host(); using var http = new HttpClient(host);
        var errors = new StringWriter(); var commands = host.Commands(http, new(), errors);
        commands.Picker = (_, _) => Task.FromResult<string?>("models.dev:unoffered");
        commands.SecretPanelPrompt = (_, _) => throw new InvalidOperationException("must not prompt");
        await commands.TryHandleAsync("/provider", null, default);
        Assert.Empty(host.Posts); Assert.Contains("invalid_provider", errors.ToString());
    }

    [Fact]
    public async Task Cancellation_after_secret_entry_never_posts()
    {
        using var host = new Host(); using var http = new HttpClient(host); using var cancellation = new CancellationTokenSource();
        var commands = host.Commands(http, new(), new());
        commands.SecretPanelPrompt = (_, _) => { cancellation.Cancel(); return Task.FromResult<string?>("synthetic-key"); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commands.TryHandleAsync("/provider", "openai", cancellation.Token));
        Assert.Empty(host.Posts);
    }

    [Fact]
    public void Help_and_completion_offer_one_provider_command()
    {
        Assert.Empty(ChatCommandCatalog.FindMatches("/backend")); Assert.Empty(ChatCommandCatalog.FindMatches("/key"));
        Assert.Single(ChatCommandCatalog.FindMatches("/provider"));
        var help = string.Join('\n', TuiSurfaces.Help(120));
        Assert.Contains("/provider", help); Assert.DoesNotContain("/backend", help); Assert.DoesNotContain("/key", help);
    }
}
