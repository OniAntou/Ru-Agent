using System.Linq;
using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

/// <summary>The "Custom model API" editor for named custom providers (ADR 0034).</summary>
public sealed class CustomProviderEditorTests
{
    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1")]
    [InlineData("https://gw.example/v1/chat/completions", "https://gw.example/v1")]
    [InlineData("https://relay.example/v1/messages/", "https://relay.example/v1")]
    [InlineData("not a url", null)]
    [InlineData("https://user:pw@gw.example/v1", null)]
    public void The_editor_previews_the_base_url_the_host_will_store(string typed, string? expected)
    {
        Assert.Equal(expected, CustomProviderPolicy.NormalizeBaseUrl(typed));
    }

    [Theory]
    [InlineData("https://gw.example/v1", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:8000/v1", true)]
    [InlineData("http://192.168.1.5:8000/v1", false)]
    public void Keys_go_only_over_https_or_to_this_computer(string url, bool allowed)
    {
        Assert.Equal(allowed, CustomProviderPolicy.AllowsKey(url));
    }

    [Fact]
    public void Validation_names_the_first_field_that_is_wrong()
    {
        CustomProviderDraft Draft(string id = "home", string name = "Home", string url = "http://localhost:11434/v1",
            params CustomProviderModelDraft[] models) =>
            new(id, name, url, CustomProviderPolicy.ChatCompletions, models);

        Assert.Null(CustomProviderPolicy.Validate(Draft(models: new CustomProviderModelDraft("qwen3:8b", "16,384")), []));
        Assert.StartsWith("Provider ID", CustomProviderPolicy.Validate(Draft(id: "Home"), []));
        Assert.StartsWith("Provider ID", CustomProviderPolicy.Validate(Draft(), ["home"]));
        Assert.StartsWith("Display name", CustomProviderPolicy.Validate(Draft(name: " "), []));
        Assert.StartsWith("Base URL", CustomProviderPolicy.Validate(Draft(url: "localhost"), []));
        Assert.StartsWith("Models", CustomProviderPolicy.Validate(Draft(models: [new("a", ""), new("a", "")]), []));
        Assert.StartsWith("Models", CustomProviderPolicy.Validate(Draft(models: new CustomProviderModelDraft("a", "100")), []));
        Assert.Equal(16384, CustomProviderPolicy.ToDto(Draft(models: new CustomProviderModelDraft("a", "16,384"))).Models[0].ContextWindow);
    }

    [Fact]
    public async Task Saving_a_new_provider_keeps_the_others_and_sends_its_key_once()
    {
        var backend = new FakeBackend();
        var editor = new CustomProviderEditorViewModel(backend);
        var closed = false;
        editor.Closed += (_, changed) => closed = changed;
        editor.Open(Settings(Provider("work", "Work vLLM")), null);

        editor.ProviderId = "home";
        editor.DisplayName = "Ollama at home";
        editor.BaseUrl = "http://localhost:11434";
        editor.ApiKey = "home-secret";
        editor.AddModelCommand.Execute(null);
        editor.Models[0].Id = "qwen3:8b";
        editor.Models[0].ContextWindow = "16384";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        var saved = Assert.Single(backend.Saves);
        Assert.Equal(new[] { "work", "home" }, saved.Providers.Select(provider => provider.Id));
        var home = saved.Providers[1];
        Assert.Equal("http://localhost:11434/v1", home.BaseUrl);
        Assert.Equal(16384, home.Models.Single().ContextWindow);
        Assert.Equal("home", saved.KeyProviderId);
        Assert.Equal("home-secret", saved.Key);
        Assert.Equal(string.Empty, editor.ApiKey);
        Assert.True(editor.HasSavedKey);
    }

    [Fact]
    public async Task An_invalid_draft_is_explained_and_never_sent()
    {
        var backend = new FakeBackend();
        var editor = new CustomProviderEditorViewModel(backend);
        editor.Open(Settings(Provider("home", "Home")), null);
        editor.ProviderId = "home";
        editor.DisplayName = "Again";
        editor.BaseUrl = "http://localhost:1/v1";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Empty(backend.Saves);
        Assert.Contains("already used", editor.StatusMessage);
        Assert.Equal(ProviderConnectionTest.ErrorTone, editor.StatusTone);
    }

    [Fact]
    public void The_editor_warns_before_a_key_would_go_over_plain_http_to_another_computer()
    {
        var editor = new CustomProviderEditorViewModel(new FakeBackend());
        editor.Open(Settings(), null);
        editor.BaseUrl = "http://192.168.1.5:8000/v1";
        Assert.False(editor.IsInsecureKeyWarningVisible);
        editor.ApiKey = "secret";
        Assert.True(editor.IsInsecureKeyWarningVisible);
        editor.BaseUrl = "https://gw.example/v1";
        Assert.False(editor.IsInsecureKeyWarningVisible);
    }

    [Fact]
    public async Task Fetching_saves_first_then_offers_the_servers_models_without_storing_them()
    {
        var backend = new FakeBackend
        {
            Discovery = new ProviderCatalogSnapshot
            {
                Status = "ok",
                Catalog = [new ProviderCatalogModel { Id = "qwen3:8b", ContextLength = 32768 }, new ProviderCatalogModel { Id = "llama3" }],
            },
        };
        var editor = new CustomProviderEditorViewModel(backend);
        editor.Open(Settings(), null);
        editor.ProviderId = "vllm";
        editor.DisplayName = "vLLM";
        editor.BaseUrl = "http://localhost:8000/v1";

        await editor.FetchModelsCommand.ExecuteAsync(null);

        Assert.Single(backend.Saves);
        Assert.Equal("custom:vllm", backend.DiscoveredBackend);
        Assert.Equal(new[] { "qwen3:8b", "llama3" }, editor.Discovered.Select(row => row.Id));
        Assert.Empty(editor.Models);

        editor.AddDiscoveredCommand.Execute(editor.Discovered[0]);
        Assert.Equal("qwen3:8b", editor.Models.Single().Id);
        Assert.Equal("32768", editor.Models.Single().ContextWindow);
        Assert.False(editor.Discovered[0].CanAdd);
    }

    [Fact]
    public async Task A_failed_fetch_reads_as_the_connection_test_result()
    {
        var backend = new FakeBackend { Discovery = new ProviderCatalogSnapshot { Status = "error", ErrorCode = "provider_insecure_endpoint" } };
        var editor = new CustomProviderEditorViewModel(backend);
        editor.Open(Settings(Provider("lan", "LAN")), "lan");

        await editor.FetchModelsCommand.ExecuteAsync(null);

        Assert.Contains("plain http://", editor.StatusMessage);
        Assert.Equal(ProviderConnectionTest.ErrorTone, editor.StatusTone);
    }

    [Fact]
    public async Task Deleting_removes_the_key_before_the_provider()
    {
        var backend = new FakeBackend();
        var editor = new CustomProviderEditorViewModel(backend);
        var settings = Settings(Provider("home", "Home"), Provider("work", "Work"));
        settings.CustomProviderKeys = ["home"];
        editor.Open(settings, "home");

        await editor.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "custom:home" }, backend.RemovedKeys);
        Assert.Equal(new[] { "work" }, backend.Saves.Single().Providers.Select(provider => provider.Id));
    }

    [Fact]
    public void Named_providers_become_model_picker_groups_under_their_own_names()
    {
        var groups = ProviderGroupDefinitions.WithModelsDev(
            [new ModelsDevEntry("custom:home", "Ollama at home"), new ModelsDevEntry("openrouter", "OpenRouter")]);

        var home = groups.Single(group => group.Backend == "custom:home");
        Assert.Equal("custom:home", home.Alias);
        Assert.Equal("Ollama at home", home.DisplayName);
        Assert.Contains(groups, group => group.Backend == "models.dev:openrouter");
        Assert.Equal("qwen3:8b", ProviderModelSelection.GetSelectedModel(
            new AppSettingsDto { BackendChoice = "custom:home", Model = "qwen3:8b" }, "custom:home"));
        Assert.Equal("custom:home", ProviderSetupGuidance.NormalizeProviderChoice("custom:home"));
    }

    [Fact]
    public void Settings_lists_and_edits_named_providers_in_the_custom_editor()
    {
        var xaml = GuiSource.Text("Views", "SettingsView.xaml");
        Assert.Contains("views:CustomProviderEditorView x:Name=\"CustomProviderEditor\"", xaml);
        Assert.Contains("ViewModel.IsCustomEditorOpen", xaml);
        var editor = GuiSource.Xml("Views", "CustomProviderEditorView.xaml");
        foreach (var name in new[] { "ProviderIdBox", "DisplayNameBox", "BaseUrlBox", "ProtocolBox", "ApiKeyBox", "FetchModelsButton", "AddModelButton", "ModelsList" })
            GuiSource.Require(editor, name);
        var providers = GuiSource.Text("ViewModels", "SettingsViewModel.Providers.cs");
        Assert.Contains("CustomProviderPolicy.IsCustomChoice(choice)", providers);
    }

    private static AppSettingsDto Settings(params CustomProviderDto[] providers) => new()
    {
        BackendChoice = "openai",
        CustomProviders = providers.ToList(),
        CustomProviderKeys = [],
    };

    private static CustomProviderDto Provider(string id, string name) => new()
    {
        Id = id,
        DisplayName = name,
        BaseUrl = "http://localhost:8000/v1",
        Protocol = CustomProviderPolicy.ChatCompletions,
        Models = [new CustomProviderModelDto { Id = "m" }],
    };

    private sealed record Save(IReadOnlyList<CustomProviderDto> Providers, string? KeyProviderId, string? Key);

    private sealed class FakeBackend : ICustomProviderBackend
    {
        public List<Save> Saves { get; } = [];
        public List<string> RemovedKeys { get; } = [];
        public ProviderCatalogSnapshot? Discovery { get; init; }
        public string? DiscoveredBackend { get; private set; }

        public Task<AppSettingsDto?> GetSettingsAsync() => Task.FromResult<AppSettingsDto?>(null);

        public Task<CustomProviderSaveResult> SaveAsync(IReadOnlyList<CustomProviderDto> providers, string? keyProviderId, string? apiKey)
        {
            Saves.Add(new Save(providers, keyProviderId, apiKey));
            return Task.FromResult(new CustomProviderSaveResult(true, null, "Settings saved."));
        }

        public Task<ProviderCatalogSnapshot?> DiscoverAsync(string backend, CancellationToken cancellationToken)
        {
            DiscoveredBackend = backend;
            return Task.FromResult(Discovery);
        }

        public Task<bool> RemoveKeyAsync(string backend)
        {
            RemovedKeys.Add(backend);
            return Task.FromResult(true);
        }
    }
}
