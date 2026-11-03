using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

public sealed partial class ChatModelPickerViewModelTests
{
    private const string Backend = "models.dev:openai";
    private const string Model = "gpt-test";

    [Fact]
    public async Task Load_adopts_the_stored_selection_and_its_catalog_effort()
    {
        var (viewModel, backend, catalog, grouped) = NewViewModel();

        await viewModel.LoadAsync();

        Assert.Equal(Backend, viewModel.Backend);
        Assert.Equal(Model, viewModel.ModelLabel);
        Assert.Equal(Backend, catalog.Backend);
        Assert.Equal((Backend, Model), grouped.Selection);
        Assert.Equal("high", viewModel.EffortLabel);
        Assert.Equal(["default", "low", "high"], viewModel.ReasoningOptions().Select(option => option.Value));
        // The published context length comes from the grouped catalog, once it lists the model.
        Assert.Null(viewModel.ContextLength());
        grouped.Publish(Model, withCatalog: true);
        Assert.Equal(128000, viewModel.ContextLength());
        Assert.Same(viewModel.CurrentSettings, backend.Settings);
    }

    [Fact]
    public async Task Provider_setup_is_offered_only_once_the_selection_is_read_and_empty()
    {
        var (viewModel, backend, _, _) = NewViewModel();
        Assert.False(viewModel.NeedsProviderSetup);

        backend.Settings.Model = string.Empty;
        await viewModel.LoadAsync();
        Assert.True(viewModel.NeedsProviderSetup);

        await viewModel.SelectModelAsync(Model, Backend);
        Assert.False(viewModel.NeedsProviderSetup);
    }

    [Fact]
    public async Task A_configured_selection_never_offers_provider_setup()
    {
        var (viewModel, _, _, _) = NewViewModel();

        await viewModel.LoadAsync();

        Assert.False(viewModel.NeedsProviderSetup);
    }

    [Fact]
    public async Task A_stored_effort_survives_until_the_catalog_arrives()
    {
        var (viewModel, _, catalog, _) = NewViewModel();
        catalog.State = new ProviderCatalogState { Status = "error", Backend = Backend };
        await viewModel.LoadAsync();
        await viewModel.ApplyEffortAsync("high");

        viewModel.RefreshEffort();

        // No catalog yet: "high" cannot be judged against a default-only list.
        Assert.Equal("high", viewModel.EffortLabel);
    }

    [Fact]
    public async Task A_failed_effort_save_falls_back_and_says_so()
    {
        var (viewModel, backend, _, _) = NewViewModel();
        await viewModel.LoadAsync();
        backend.ReasoningSaveSucceeds = false;

        await viewModel.ApplyEffortAsync("low");

        Assert.Equal("high", viewModel.EffortLabel);
        Assert.EndsWith("(Save failed)", viewModel.EffortAutomationName, StringComparison.Ordinal);
        // The save is bound to the catalog identity, never the backend name.
        Assert.Equal(("low", Model, "identity-1"), Assert.Single(backend.ReasoningSaves));
    }

    [Fact]
    public async Task A_model_is_saved_before_it_is_published_and_resets_the_effort()
    {
        var (viewModel, backend, catalog, grouped) = NewViewModel();
        await viewModel.LoadAsync();
        int changes = 0;
        viewModel.SelectionChanged += () => changes++;

        await viewModel.SelectModelAsync("other-model", "models.dev:anthropic");

        Assert.Equal(("models.dev:anthropic", "other-model"), Assert.Single(backend.ModelSaves));
        Assert.Equal("other-model", viewModel.ModelLabel);
        Assert.Equal("models.dev:anthropic", catalog.Backend);
        Assert.Equal(("models.dev:anthropic", "other-model"), grouped.Selection);
        Assert.Equal("default", viewModel.SelectedEffort);
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task A_failed_model_save_shows_a_notice_that_clears_itself()
    {
        var delay = new ManualDelay();
        var (viewModel, backend, _, _) = NewViewModel(delay.WaitAsync);
        await viewModel.LoadAsync();
        backend.ModelSaveSucceeds = false;

        await viewModel.SelectModelAsync("other-model", Backend);

        Assert.True(viewModel.HasModelSaveFailure);
        Assert.Equal(Model, viewModel.ModelLabel);
        await delay.ReleaseAsync();
        Assert.False(viewModel.HasModelSaveFailure);
    }

    [Fact]
    public async Task Grouped_publishes_are_coalesced_and_the_tree_is_built_only_while_open()
    {
        var delay = new ManualDelay();
        var (viewModel, _, _, grouped) = NewViewModel(delay.WaitAsync);
        int trees = 0;
        viewModel.PickerProjectionReady += _ => trees++;

        // Catalog publications carry selection state; only an authoritative
        // settings signal may establish a choice before the initial read.
        grouped.SetSelection(Backend, Model);
        grouped.Publish(Model);
        grouped.Publish(Model);
        grouped.Publish(Model);
        await delay.ReleaseAsync();
        Assert.Equal(0, trees);
        Assert.Equal(Model, viewModel.ModelLabel);

        // Opening paints the stale tree once; a publish while open repaints it.
        viewModel.OnPickerOpened();
        Assert.Equal(1, trees);
        grouped.Publish(Model);
        await delay.ReleaseAsync();
        Assert.Equal(2, trees);
    }

    [Fact]
    public async Task A_catalog_publish_for_another_backend_is_ignored()
    {
        var (viewModel, _, catalog, _) = NewViewModel();
        await viewModel.LoadAsync();
        await viewModel.ApplyEffortAsync("low");

        catalog.Raise(new ProviderCatalogState { Status = "ok", Backend = "models.dev:other", SelectedEffort = "high" });

        Assert.Equal("low", viewModel.EffortLabel);
    }

    [Fact]
    public async Task Search_text_reaches_the_catalog_once_typing_settles()
    {
        var delay = new ManualDelay();
        var (viewModel, _, _, grouped) = NewViewModel(delay.WaitAsync);

        viewModel.SetSearchText("g");
        viewModel.SetSearchText("gp");
        viewModel.SetSearchText("gpt");
        await delay.ReleaseAsync();

        Assert.Equal(["gpt"], grouped.SearchTexts);
    }

    private static (ChatModelPickerViewModel, FakeBackend, FakeCatalog, FakeGrouped) NewViewModel(
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var backend = new FakeBackend();
        var catalog = new FakeCatalog();
        var grouped = new FakeGrouped();
        var viewModel = new ChatModelPickerViewModel(action => action(), delay ?? ((_, _) => Task.CompletedTask));
        viewModel.Attach(backend, catalog, grouped);
        return (viewModel, backend, catalog, grouped);
    }

    private static ProviderCatalogState ReadyCatalog(string backend) => new()
    {
        Status = "ok",
        Backend = backend,
        Identity = "identity-1",
        SelectedModel = Model,
        SelectedEffort = "high",
        Models = [Model],
        Catalog =
        [
            new ProviderCatalogModel
            {
                Id = Model,
                Supported = true,
                Protocol = ProviderModelProtocols.ChatCompletions,
                ContextLength = 128000,
                Reasoning = new ProviderReasoningCapabilities
                {
                    State = ProviderReasoningCapabilities.Levels,
                    Options = ["low", "high"],
                },
            },
        ],
    };

    private sealed class FakeBackend : IChatModelBackend
    {
        public AppSettingsDto Settings { get; } = new() { BackendChoice = Backend, Model = Model };

        public Task<AppSettingsDto?>? SettingsResponse { get; set; }
        public Queue<Task<AppSettingsDto?>> SettingsResponses { get; } = new();
        public Task<bool>? ModelSaveResponse { get; set; }

        public bool ModelSaveSucceeds { get; set; } = true;
        public Task<IReadOnlyList<ModelsDevEntryDto>>? ModelsDevResponse { get; set; }

        public bool ReasoningSaveSucceeds { get; set; } = true;

        public List<(string Backend, string Model)> ModelSaves { get; } = new();

        public List<(string Effort, string Model, string Identity)> ReasoningSaves { get; } = new();

        public Task<AppSettingsDto?> GetSettingsAsync() => SettingsResponses.Count > 0
            ? SettingsResponses.Dequeue()
            : SettingsResponse ?? Task.FromResult<AppSettingsDto?>(Settings);

        public Task<IReadOnlyList<ModelsDevEntryDto>> GetModelsDevAsync() =>
            ModelsDevResponse ?? Task.FromResult<IReadOnlyList<ModelsDevEntryDto>>([new ModelsDevEntryDto { Id = "openai", Name = "OpenAI" }]);

        public Task<bool> SaveProviderModelSelectionAsync(string backend, string model)
        {
            ModelSaves.Add((backend, model));
            return ModelSaveResponse ?? Task.FromResult(ModelSaveSucceeds);
        }

        public Task<bool> SaveReasoningSettingsAsync(string effort, string model, string identity)
        {
            ReasoningSaves.Add((effort, model, identity));
            return Task.FromResult(ReasoningSaveSucceeds);
        }
    }

    private sealed class FakeCatalog : IProviderCatalogSession
    {
        public event EventHandler<ProviderCatalogState>? StateChanged;

        public string Backend { get; private set; } = string.Empty;

        public ProviderCatalogState State { get; set; } = new();

        public void SetBackend(string backend)
        {
            Backend = backend;
            State = ReadyCatalog(backend);
        }

        public Task<bool> RefreshAsync(bool force = false, bool bypassManualThrottle = false) =>
            Task.FromResult(true);

        public void Raise(ProviderCatalogState state) => StateChanged?.Invoke(this, state);
    }

    private sealed class FakeGrouped : IGroupedCatalogSession
    {
        public event EventHandler<GroupedProviderCatalogState>? StateChanged;

        public GroupedProviderCatalogState State { get; private set; } = GroupedProviderCatalogState.Empty;

        public IReadOnlyList<ProviderGroupDefinition> CurrentDefinitions { get; set; } = ProviderGroupDefinitions.All;

        public (string Backend, string Model) Selection { get; private set; }

        public List<string> SearchTexts { get; } = new();

        public void SetSelection(string backend, string model) => UpdateSelection(backend, model, authoritative: true);

        public void SynchronizeSelection(string backend, string model) => UpdateSelection(backend, model, authoritative: false);

        private void UpdateSelection(string backend, string model, bool authoritative)
        {
            if (Selection == (backend, model)) return;
            Selection = (backend, model);
            State = State with
            {
                SelectedBackend = backend,
                SelectedModel = model,
                SelectionVersion = authoritative ? State.SelectionVersion + 1 : State.SelectionVersion,
            };
            StateChanged?.Invoke(this, State);
        }

        public void SetSearchText(string searchText) => SearchTexts.Add(searchText);

        public Task SetModelsDevProvidersAsync(IReadOnlyList<ModelsDevEntry> entries) => Task.CompletedTask;

        public Task<bool> RefreshAsync(bool force = false) => Task.FromResult(true);

        public void PublishCatalog(ProviderCatalogState catalog)
        {
            State = State with
            {
                SelectedBackend = Backend,
                SelectedModel = Model,
                Catalogs = new Dictionary<string, ProviderCatalogState> { [catalog.Backend] = catalog },
            };
            StateChanged?.Invoke(this, State);
        }

        public void Publish(string model, bool withCatalog = false)
        {
            State = State with
            {
                SelectedBackend = Backend,
                SelectedModel = model,
                Catalogs = withCatalog
                    ? new Dictionary<string, ProviderCatalogState>(StringComparer.Ordinal) { [Backend] = ReadyCatalog(Backend) }
                    : State.Catalogs,
            };
            StateChanged?.Invoke(this, State);
        }
    }

    private sealed class ManualDelay
    {
        private readonly List<TaskCompletionSource> _waiting = new();

        public Task WaitAsync(TimeSpan delay, CancellationToken token)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiting) _waiting.Add(gate);
            return gate.Task.WaitAsync(token);
        }

        public async Task ReleaseAsync()
        {
            TaskCompletionSource[] gates;
            lock (_waiting)
            {
                gates = _waiting.ToArray();
                _waiting.Clear();
            }

            foreach (var gate in gates) gate.TrySetResult();
            // Let the released continuations run before asserting.
            for (int i = 0; i < 20; i++) await Task.Delay(5);
        }
    }
}
