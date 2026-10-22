using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed partial class SettingsViewModelTests
{
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
    public async Task Custom_action_opens_the_endpoint_editor_without_a_catalog_selection()
    {
        using var vm = NewViewModel();
        await vm.AttachAsync(new FakeBackend(), new FakeCatalog());
        vm.AddCustomProviderCommand.Execute(null);
        Assert.True(vm.IsProviderEditorOpen);
        Assert.True(vm.IsCustomProviderVisible);
        Assert.False(vm.IsModelsDevKeyVisible);
        Assert.Equal("custom", vm.ProviderChoice);
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
        await vm.AttachAsync(new FakeBackend { SaveResult = false }, new FakeCatalog());
        vm.AddCustomProviderCommand.Execute(null);
        await vm.ReturnToProvidersCommand.ExecuteAsync(null);
        Assert.True(vm.IsProviderEditorOpen);
        Assert.Contains("Could not save", vm.ProviderTestMessage);
        Assert.True(vm.IsProviderNavigationEnabled);
    }
}
