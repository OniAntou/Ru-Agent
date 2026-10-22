using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

public sealed partial class SettingsViewModelTests
{
    [Fact]
    public void Standard_is_the_default_preset_and_dark_the_default_theme()
    {
        using var viewModel = NewViewModel();

        Assert.Equal("standard", viewModel.PresetChoice);
        Assert.Equal("dark", viewModel.ThemeChoice);
    }

    [Fact]
    public async Task A_fresh_profile_shows_the_key_row_for_the_provider_the_catalog_resolves()
    {
        // Baseline defect B4: the unset choice resolved to the first catalog provider,
        // but the key row was computed before that and stayed hidden.
        var backend = new FakeBackend { Settings = new AppSettingsDto { BackendChoice = string.Empty } };
        using var viewModel = NewViewModel();

        await viewModel.AttachAsync(backend, new FakeCatalog());

        Assert.Equal("models.dev:302ai", viewModel.ProviderChoice);
        Assert.Equal("302.AI", viewModel.ProviderButtonText);
        Assert.True(viewModel.IsModelsDevKeyVisible);
        Assert.Equal("302.AI API key", viewModel.ModelsDevKeyLabel);
        Assert.False(viewModel.IsCustomProviderVisible);
        Assert.False(viewModel.IsModelsDevSignOutVisible);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task A_stored_legacy_alias_with_a_key_keeps_its_name()
    {
        // An existing OpenAI setup has no picker row but must keep working and be named,
        // not show the label of whichever row was rebuilt last.
        var backend = new FakeBackend { Settings = new AppSettingsDto { BackendChoice = "openai", HasOpenAiKey = true } };
        using var viewModel = NewViewModel();

        await viewModel.AttachAsync(backend, new FakeCatalog());

        Assert.Equal("openai", viewModel.ProviderChoice);
        Assert.Equal("OpenAI", viewModel.ProviderButtonText);
        Assert.All(viewModel.ProviderRows, row => Assert.False(row.IsSelected));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task A_keyless_legacy_alias_reads_as_unset_so_a_new_user_gets_a_key_box(string stored)
    {
        // A fresh Host stores "openai". With no key it has neither a picker row nor a key
        // row, so a new user had nowhere to type a key and saw a network error instead.
        var backend = new FakeBackend { Settings = new AppSettingsDto { BackendChoice = stored } };
        using var viewModel = NewViewModel();

        await viewModel.AttachAsync(backend, new FakeCatalog());

        Assert.Equal("models.dev:302ai", viewModel.ProviderChoice);
        Assert.True(viewModel.IsModelsDevKeyVisible);
        Assert.Equal("302.AI API key", viewModel.ModelsDevKeyLabel);
    }

    [Fact]
    public async Task A_catalog_failure_for_a_provider_without_a_key_asks_for_the_key()
    {
        var catalog = new FakeCatalog();
        var dispatched = new List<Action>();
        using var viewModel = new SettingsViewModel(dispatched.Add, (_, _) => Task.CompletedTask);
        await viewModel.AttachAsync(new FakeBackend { Settings = new AppSettingsDto { BackendChoice = string.Empty } }, catalog);

        catalog.Raise(new ProviderCatalogState { Status = "error", ErrorCode = "catalog_unavailable" });
        foreach (var action in dispatched.ToArray()) action();

        Assert.Equal("Add a 302.AI API key above to load its models.", viewModel.ProviderTestMessage);
        Assert.Equal(ProviderConnectionTest.NeutralTone, viewModel.ProviderTestTone);

        // A timeout is not a missing key, even for a provider without one.
        catalog.Raise(new ProviderCatalogState { Status = "error", ErrorCode = "catalog_timeout" });
        foreach (var action in dispatched.ToArray()) action();
        Assert.Contains("did not answer in time", viewModel.ProviderTestMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_in_provider_leads_the_picker_and_offers_sign_out()
    {
        var backend = new FakeBackend
        {
            Settings = new AppSettingsDto { BackendChoice = "models.dev:openai" },
            KeyIds = ["openai"],
        };
        using var viewModel = NewViewModel();

        await viewModel.AttachAsync(backend, new FakeCatalog());

        Assert.Equal("models.dev:openai", viewModel.ProviderRows[0].Value);
        Assert.True(viewModel.ProviderRows[0].HasKey);
        Assert.True(viewModel.ProviderRows[0].IsSelected);
        Assert.True(viewModel.IsModelsDevSignOutVisible);
    }

    [Fact]
    public async Task A_filter_with_no_match_keeps_the_count_and_reports_no_matches()
    {
        using var viewModel = NewViewModel();
        await viewModel.AttachAsync(new FakeBackend(), new FakeCatalog());

        viewModel.ProviderFilter = "zzz-not-a-provider";

        Assert.True(viewModel.HasNoProviderMatches);
        Assert.Contains("0", viewModel.ProviderHeaderText, StringComparison.Ordinal);
        Assert.All(viewModel.ProviderRows, row => Assert.False(row.IsVisible));

        viewModel.ProviderFilter = string.Empty;
        Assert.False(viewModel.HasNoProviderMatches);
    }

    [Fact]
    public async Task Selecting_a_provider_switches_the_catalog_closes_the_picker_and_autosaves()
    {
        var backend = new FakeBackend();
        var catalog = new FakeCatalog();
        using var viewModel = NewViewModel();
        await viewModel.AttachAsync(backend, catalog);
        bool closed = false;
        viewModel.ProviderPickerCommitted += () => closed = true;
        viewModel.ProviderFilter = "anth";

        viewModel.SelectProviderCommand.Execute(viewModel.ProviderRows.Single(row => row.Value == "models.dev:anthropic"));
        await backend.WaitForSaveAsync();

        Assert.True(closed);
        Assert.Equal(string.Empty, viewModel.ProviderFilter);
        Assert.Equal("models.dev:anthropic", catalog.Backend);
        Assert.Equal("models.dev:anthropic", backend.Saved.Last().BackendChoice);
    }

    [Fact]
    public async Task Edits_during_a_load_do_not_autosave()
    {
        var backend = new FakeBackend();
        using var viewModel = NewViewModel();

        await viewModel.AttachAsync(backend, new FakeCatalog());

        // The load itself writes the URL and account fields; only user edits save.
        Assert.Empty(backend.Saved);
        viewModel.CustomUrl = "http://127.0.0.1:9000/v1";
        await backend.WaitForSaveAsync();
        Assert.Equal("http://127.0.0.1:9000/v1", backend.Saved.Single().CustomUrl);
    }

    [Fact]
    public async Task A_quiet_key_box_is_validated_by_the_host_and_cleared_once_saved()
    {
        var backend = new FakeBackend
        {
            Settings = new AppSettingsDto { BackendChoice = "models.dev:openai" },
            KeyResult = new ProviderKeyProbeResult("valid"),
        };
        var catalog = new FakeCatalog();
        using var viewModel = NewViewModel();
        await viewModel.AttachAsync(backend, catalog);

        viewModel.ModelsDevApiKey = "sk-synthetic-test-key";
        await backend.WaitForValidationAsync();
        await Task.Yield();

        var (providerId, key) = Assert.Single(backend.Validations);
        Assert.StartsWith(ProviderSetupGuidance.ModelsDevPrefix, providerId, StringComparison.Ordinal);
        Assert.Equal("sk-synthetic-test-key", key);
        // Validation never removes the previously working key.
        Assert.Empty(backend.Removed);
        Assert.Equal(string.Empty, viewModel.ModelsDevApiKey);
        Assert.Equal(ProviderConnectionTest.SuccessTone, viewModel.ModelsDevKeyStatusTone);
        Assert.True(viewModel.IsModelsDevSignOutVisible);
    }

    [Fact]
    public async Task Saving_a_key_for_the_resolved_provider_also_stores_that_provider()
    {
        // The Host's default ("openai", no key) resolves to 302.AI in the editor. The key
        // route stores only the key, so without this the Host stayed on OpenAI and the
        // chat kept reporting a missing key.
        var backend = new FakeBackend
        {
            Settings = new AppSettingsDto { BackendChoice = "openai" },
            KeyResult = new ProviderKeyProbeResult("valid"),
        };
        using var viewModel = NewViewModel();
        await viewModel.AttachAsync(backend, new FakeCatalog());

        viewModel.ModelsDevApiKey = "sk-synthetic-test-key";
        await backend.WaitForValidationAsync();
        await backend.WaitForSaveAsync();

        Assert.Equal("models.dev:302ai", Assert.Single(backend.Saved).BackendChoice);
    }

    [Fact]
    public async Task Saving_a_key_for_the_stored_provider_does_not_rewrite_the_settings()
    {
        var backend = new FakeBackend
        {
            Settings = new AppSettingsDto { BackendChoice = "models.dev:openai" },
            KeyResult = new ProviderKeyProbeResult("valid"),
        };
        using var viewModel = NewViewModel();
        await viewModel.AttachAsync(backend, new FakeCatalog());

        viewModel.ModelsDevApiKey = "sk-synthetic-test-key";
        await backend.WaitForValidationAsync();
        await Task.Yield();

        Assert.Empty(backend.Saved);
    }

    [Fact]
    public async Task A_rejected_key_keeps_the_saved_key_and_says_so()
    {
        var backend = new FakeBackend
        {
            Settings = new AppSettingsDto { BackendChoice = "models.dev:openai" },
            KeyResult = new ProviderKeyProbeResult("invalid"),
        };
        using var viewModel = NewViewModel();
        await viewModel.AttachAsync(backend, new FakeCatalog());

        viewModel.ModelsDevApiKey = "sk-synthetic-bad-key";
        await backend.WaitForValidationAsync();
        await Task.Yield();

        Assert.Equal("Invalid key; saved key unchanged.", viewModel.ModelsDevKeyStatus);
        Assert.Equal(ProviderConnectionTest.ErrorTone, viewModel.ModelsDevKeyStatusTone);
        Assert.Empty(backend.Removed);
    }

    [Fact]
    public async Task Sign_out_removes_the_active_key_reloads_and_then_confirms()
    {
        var backend = new FakeBackend
        {
            Settings = new AppSettingsDto { BackendChoice = "models.dev:openai" },
            KeyIds = ["openai"],
        };
        using var viewModel = NewViewModel();
        await viewModel.AttachAsync(backend, new FakeCatalog());
        backend.KeyIds = [];

        await viewModel.SignOutCommand.ExecuteAsync(null);

        Assert.Equal("models.dev:openai", Assert.Single(backend.Removed));
        Assert.StartsWith("Signed out of OpenAI", viewModel.ModelsDevKeyStatus, StringComparison.Ordinal);
        Assert.False(viewModel.IsModelsDevSignOutVisible);
        Assert.True(viewModel.IsSignOutEnabled);
    }

    [Fact]
    public async Task Removing_the_custom_key_clears_its_box_and_reloads()
    {
        var backend = new FakeBackend { Settings = new AppSettingsDto { BackendChoice = "custom", HasCustomKey = true } };
        using var viewModel = NewViewModel();
        await viewModel.AttachAsync(backend, new FakeCatalog());
        viewModel.CustomApiKey = "sk-synthetic-custom";
        int loads = backend.SettingsReads;

        await viewModel.RemoveProviderCommand.ExecuteAsync("custom");

        Assert.Equal("custom", Assert.Single(backend.Removed));
        Assert.Equal(string.Empty, viewModel.CustomApiKey);
        Assert.Equal(loads + 1, backend.SettingsReads);
    }

    [Fact]
    public async Task The_connection_test_saves_before_it_refreshes_and_reports_the_outcome()
    {
        var backend = new FakeBackend();
        var catalog = new FakeCatalog { NextState = new ProviderCatalogState { Status = "ok", Models = ["m1"] } };
        catalog.OnRefresh = () => Assert.NotEmpty(backend.Saved);
        using var viewModel = NewViewModel();
        await viewModel.AttachAsync(backend, catalog);

        await viewModel.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(catalog.ForcedRefreshes > 0);
        Assert.NotEqual(ProviderConnectionTestPresentation.TestingMessage, viewModel.ProviderTestMessage);
        Assert.True(viewModel.IsProviderTestEnabled);
    }

    [Fact]
    public async Task A_background_catalog_failure_is_shown_but_a_background_success_is_not()
    {
        var catalog = new FakeCatalog();
        var dispatched = new List<Action>();
        using var viewModel = new SettingsViewModel(dispatched.Add, (_, _) => Task.CompletedTask);
        // A provider with a stored key, so a failure is a real failure rather than the
        // missing-key prompt (A_catalog_failure_for_a_provider_without_a_key_asks_for_the_key).
        await viewModel.AttachAsync(
            new FakeBackend { Settings = new AppSettingsDto { BackendChoice = "models.dev:openai" }, KeyIds = ["openai"] },
            catalog);
        string initial = viewModel.ProviderTestMessage;

        catalog.Raise(new ProviderCatalogState { Status = "ok", Models = ["m1"] });
        Assert.Empty(dispatched);

        catalog.Raise(new ProviderCatalogState { Status = "error", ErrorCode = "catalog_unreachable" });
        Assert.Equal(initial, viewModel.ProviderTestMessage);
        Assert.Single(dispatched)();
        Assert.Equal(ProviderConnectionTest.ErrorTone, viewModel.ProviderTestTone);
    }

    [Fact]
    public async Task Disposal_releases_the_catalog_subscription()
    {
        var catalog = new FakeCatalog();
        var viewModel = NewViewModel();
        await viewModel.AttachAsync(new FakeBackend(), catalog);
        Assert.Equal(1, catalog.Subscribers);

        viewModel.Dispose();

        Assert.Equal(0, catalog.Subscribers);
    }

    [Fact]
    public async Task Reattaching_moves_the_subscription_to_the_new_catalog()
    {
        var first = new FakeCatalog();
        var second = new FakeCatalog();
        using var viewModel = NewViewModel();

        await viewModel.AttachAsync(new FakeBackend(), first);
        await viewModel.AttachAsync(new FakeBackend(), second);

        Assert.Equal(0, first.Subscribers);
        Assert.Equal(1, second.Subscribers);
    }

    [Theory]
    [InlineData("plan", "plan", AgentPermissionPreset.Plan)]
    [InlineData("full", "full_access", AgentPermissionPreset.FullAccess)]
    [InlineData("standard", "standard", AgentPermissionPreset.Standard)]
    public void Selecting_a_preset_publishes_it(string tag, string expectedChoice, AgentPermissionPreset expected)
    {
        using var viewModel = NewViewModel();
        AgentPermissionPreset? published = null;
        viewModel.PresetChanged += preset => published = preset;

        viewModel.SelectPresetCommand.Execute(tag);

        Assert.Equal(expectedChoice, viewModel.PresetChoice);
        Assert.Equal(expected, published);
    }

    private static SettingsViewModel NewViewModel() =>
        new(action => action(), (_, _) => Task.CompletedTask);

    private sealed class FakeBackend : ISettingsBackend
    {
        private TaskCompletionSource _saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _validated = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AppSettingsDto Settings { get; set; } = new() { BackendChoice = "models.dev:openai" };

        public IReadOnlyList<string> KeyIds { get; set; } = [];

        public ProviderKeyProbeResult KeyResult { get; set; } = new("valid");
        public bool SaveResult { get; set; } = true;

        public List<ProviderSettingsUpdateDto> Saved { get; } = new();

        public List<(string ProviderId, string Key)> Validations { get; } = new();

        public List<string> Removed { get; } = new();

        public int SettingsReads { get; private set; }

        public Task<AppSettingsDto?> GetSettingsAsync()
        {
            SettingsReads++;
            return Task.FromResult<AppSettingsDto?>(Settings);
        }

        public Task<IReadOnlyList<ModelsDevEntryDto>> GetModelsDevAsync() =>
            Task.FromResult<IReadOnlyList<ModelsDevEntryDto>>(
            [
                new ModelsDevEntryDto { Id = "openai", Name = "OpenAI" },
                new ModelsDevEntryDto { Id = "anthropic", Name = "Anthropic" },
                new ModelsDevEntryDto { Id = "302ai", Name = "302.AI" },
            ]);

        public Task<IReadOnlyList<string>> GetModelsDevKeysAsync() => Task.FromResult(KeyIds);

        public Task<bool> SaveProviderSettingsAsync(ProviderSettingsUpdateDto settings)
        {
            Saved.Add(settings);
            _saved.TrySetResult();
            return Task.FromResult(SaveResult);
        }

        public Task<ProviderKeyProbeResult> ValidateAndSaveModelsDevKeyAsync(string providerId, string key, CancellationToken cancellationToken)
        {
            Validations.Add((providerId, key));
            if (KeyResult.Code == "valid") KeyIds = KeyIds.Append(providerId[ProviderSetupGuidance.ModelsDevPrefix.Length..]).ToArray();
            _validated.TrySetResult();
            return Task.FromResult(KeyResult);
        }

        public Task<bool> RemoveProviderAsync(string backendChoice)
        {
            Removed.Add(backendChoice);
            return Task.FromResult(true);
        }

        public async Task WaitForSaveAsync()
        {
            await _saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task WaitForValidationAsync()
        {
            await _validated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _validated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class FakeCatalog : IProviderCatalogSession
    {
        private EventHandler<ProviderCatalogState>? _stateChanged;

        public event EventHandler<ProviderCatalogState>? StateChanged
        {
            add { _stateChanged += value; Subscribers++; }
            remove { _stateChanged -= value; Subscribers--; }
        }

        public int Subscribers { get; private set; }

        public string Backend { get; private set; } = string.Empty;

        public int ForcedRefreshes { get; private set; }

        public ProviderCatalogState NextState { get; set; } = new();

        public Action? OnRefresh { get; set; }

        public ProviderCatalogState State { get; private set; } = new();

        public void SetBackend(string backend) => Backend = backend;

        public Task<bool> RefreshAsync(bool force = false)
        {
            if (force) ForcedRefreshes++;
            OnRefresh?.Invoke();
            State = NextState;
            return Task.FromResult(true);
        }

        public void Raise(ProviderCatalogState state) => _stateChanged?.Invoke(this, state);
    }
}
