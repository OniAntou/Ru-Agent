using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed partial class SettingsViewModelTests
{
    [Fact]
    public async Task Back_keeps_the_provider_editor_disabled_through_save_and_reload()
    {
        const string savedUrl = "http://127.0.0.1:8000/v1";
        var reloadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend
        {
            Settings = new AppSettingsDto { BackendChoice = "custom", CustomUrl = savedUrl },
        };
        backend.GetSettingsOverride = async read =>
        {
            if (read == 2)
            {
                reloadEntered.TrySetResult();
                await releaseReload.Task.ConfigureAwait(false);
            }
            return backend.Settings;
        };
        using var vm = NewViewModel();
        await vm.AttachAsync(backend, new FakeCatalog());
        vm.EditProviderCommand.Execute("custom");

        var returning = vm.ReturnToProvidersCommand.ExecuteAsync(null);
        await reloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsLoading);
        Assert.True(vm.IsProviderEditorOpen);
        Assert.False(vm.IsProviderManagementEnabled);
        Assert.NotEmpty(backend.Saved);
        Assert.All(backend.Saved, saved => Assert.Equal(savedUrl, saved.CustomUrl));

        releaseReload.TrySetResult();
        await returning.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsProviderOverviewVisible);
        Assert.Equal(savedUrl, vm.CustomUrl);
    }

    [Fact]
    public void Provider_editor_binds_all_controls_to_the_loading_and_navigation_gate()
    {
        var editor = GuiSource.NamedElements(GuiSource.Xml("Views", "SettingsView.xaml"))["ProviderEditor"];

        Assert.Equal("ContentControl", editor.Name.LocalName);
        Assert.Equal("{x:Bind ViewModel.IsProviderManagementEnabled, Mode=OneWay}", editor.Attribute("IsEnabled")?.Value);
        Assert.Equal("StackPanel", editor.Elements().Single().Name.LocalName);
    }

    [Fact]
    public async Task Fresh_settings_open_the_overview_and_keep_custom_out_of_the_catalog_picker()
    {
        using var vm = NewViewModel();
        await vm.AttachAsync(new FakeBackend(), new FakeCatalog());
        Assert.True(vm.IsProviderOverviewVisible);
        Assert.False(vm.IsProviderEditorOpen);
        Assert.True(vm.HasNoConfiguredProviders);
        Assert.DoesNotContain(vm.CatalogProviderRows, row => row.Value == "custom");
        Assert.Equal(3, vm.CatalogProviderRows.Count);
    }

    [Fact]
    public async Task Overview_lists_saved_keys_including_a_provider_missing_from_the_catalog()
    {
        using var vm = NewViewModel();
        await vm.AttachAsync(new FakeBackend { KeyIds = ["openai", "retired-provider"] }, new FakeCatalog());
        Assert.Equal(2, vm.ConfiguredProviders.Count);
        Assert.All(vm.ConfiguredProviders, row => Assert.Equal("Key saved", row.Status));
        Assert.False(vm.HasNoConfiguredProviders);
        Assert.Equal("models.dev:openai", vm.CatalogProviderRows[0].Value);

        vm.EditProviderCommand.Execute("models.dev:retired-provider");
        Assert.True(vm.IsProviderEditorOpen);
        Assert.Equal("models.dev:retired-provider", vm.ProviderChoice);
    }

    [Fact]
    public async Task Legacy_credentials_remain_visible_and_edit_does_not_substitute_a_catalog_identity()
    {
        using var vm = NewViewModel();
        await vm.AttachAsync(new FakeBackend { Settings = new AppSettingsDto { BackendChoice = "openai", HasOpenAiKey = true } }, new FakeCatalog());
        Assert.Contains(vm.ConfiguredProviders, row => row.Choice == "openai");
        vm.EditProviderCommand.Execute("openai");
        Assert.True(vm.IsProviderEditorOpen);
        Assert.True(vm.IsLegacyProviderVisible);
        Assert.Equal("openai", vm.ProviderChoice);
        Assert.False(vm.IsModelsDevKeyVisible);
    }

    [Fact]
    public async Task Custom_action_opens_the_custom_model_api_editor_without_changing_the_provider()
    {
        using var vm = NewViewModel();
        var editor = new RuApp.ViewModels.CustomProviderEditorViewModel(new NoCustomProviderBackend());
        vm.AttachCustomEditor(editor);
        await vm.AttachAsync(new FakeBackend(), new FakeCatalog());
        var choice = vm.ProviderChoice;

        vm.AddCustomProviderCommand.Execute(null);

        Assert.True(vm.IsCustomEditorOpen);
        Assert.False(vm.IsProviderEditorOpen);
        Assert.False(vm.IsProviderOverviewVisible);
        Assert.True(editor.IsNew);
        Assert.Equal(choice, vm.ProviderChoice);

        editor.CancelCommand.Execute(null);
        Assert.False(vm.IsCustomEditorOpen);
        Assert.True(vm.IsProviderOverviewVisible);
    }

    [Fact]
    public async Task Named_custom_providers_are_listed_and_edited_in_their_own_editor()
    {
        using var vm = NewViewModel();
        var editor = new RuApp.ViewModels.CustomProviderEditorViewModel(new NoCustomProviderBackend());
        vm.AttachCustomEditor(editor);
        var settings = new AppSettingsDto
        {
            BackendChoice = "custom:home",
            CustomProviders = [new CustomProviderDto { Id = "home", DisplayName = "Ollama at home", BaseUrl = "http://localhost:11434/v1", Models = [new() { Id = "qwen3:8b" }] }],
            CustomProviderKeys = [],
        };
        await vm.AttachAsync(new FakeBackend { Settings = settings }, new FakeCatalog());

        var row = Assert.Single(vm.ConfiguredProviders);
        Assert.Equal("custom:home", row.Choice);
        Assert.Equal("Ollama at home", row.DisplayName);
        Assert.Contains("1 model", row.Status);
        Assert.Equal("custom:home", vm.ProviderChoice);

        vm.EditProviderCommand.Execute("custom:home");
        Assert.True(vm.IsCustomEditorOpen);
        Assert.False(editor.IsNew);
        Assert.Equal("Ollama at home", editor.DisplayName);
    }

    private sealed class NoCustomProviderBackend : RuApp.ViewModels.ICustomProviderBackend
    {
        public Task<AppSettingsDto?> GetSettingsAsync() => Task.FromResult<AppSettingsDto?>(null);
        public Task<CustomProviderSaveResult> SaveAsync(IReadOnlyList<CustomProviderDto> providers, string? keyProviderId, string? apiKey) =>
            Task.FromResult(new CustomProviderSaveResult(true, null, ""));
        public Task<ProviderCatalogSnapshot?> DiscoverAsync(string backend, CancellationToken cancellationToken) =>
            Task.FromResult<ProviderCatalogSnapshot?>(null);
        public Task<bool> RemoveKeyAsync(string backend) => Task.FromResult(true);
    }

    [Fact]
    public async Task Keyless_custom_endpoint_is_configured_without_claiming_a_verified_connection()
    {
        using var vm = NewViewModel();
        await vm.AttachAsync(new FakeBackend { Settings = new AppSettingsDto { BackendChoice = "custom" } }, new FakeCatalog());
        var row = Assert.Single(vm.ConfiguredProviders);
        Assert.True(row.IsCustom);
        Assert.Equal("Endpoint configured", row.Status);
        Assert.False(vm.IsCustomSignOutVisible);
    }

    [Fact]
    public async Task Back_keeps_a_rejected_key_in_its_editor_instead_of_losing_it()
    {
        using var vm = NewViewModel();
        await vm.AttachAsync(new FakeBackend { KeyResult = new("invalid") }, new FakeCatalog());
        vm.EditProviderCommand.Execute("models.dev:openai");
        vm.ModelsDevApiKey = "synthetic-rejected-key";
        await vm.ReturnToProvidersCommand.ExecuteAsync(null);
        Assert.True(vm.IsProviderEditorOpen);
        Assert.Equal("synthetic-rejected-key", vm.ModelsDevApiKey);
        Assert.Contains("Invalid key", vm.ModelsDevKeyStatus);
        Assert.True(vm.IsProviderNavigationEnabled);
    }

    [Fact]
    public async Task Back_flushes_a_key_before_another_provider_can_be_opened()
    {
        var backend = new FakeBackend();
        using var vm = NewViewModel();
        await vm.AttachAsync(backend, new FakeCatalog());
        vm.EditProviderCommand.Execute("models.dev:openai");
        vm.ModelsDevApiKey = "synthetic-saved-key";
        await vm.ReturnToProvidersCommand.ExecuteAsync(null);
        Assert.True(vm.IsProviderOverviewVisible);
        Assert.Empty(vm.ModelsDevApiKey);
        Assert.Contains(vm.ConfiguredProviders, row => row.Choice == "models.dev:openai");
        vm.EditProviderCommand.Execute("models.dev:anthropic");
        Assert.DoesNotContain(backend.Validations, check => check.ProviderId == "models.dev:anthropic");
    }

    [Fact]
    public async Task A_failed_save_keeps_the_editor_and_gives_a_recovery_message()
    {
        using var vm = NewViewModel();
        await vm.AttachAsync(new FakeBackend { SaveResult = false, Settings = new AppSettingsDto { BackendChoice = "custom" } }, new FakeCatalog());
        vm.EditProviderCommand.Execute("custom");
        await vm.ReturnToProvidersCommand.ExecuteAsync(null);
        Assert.True(vm.IsProviderEditorOpen);
        Assert.Contains("Could not save", vm.ProviderTestMessage);
        Assert.True(vm.IsProviderNavigationEnabled);
    }
}
