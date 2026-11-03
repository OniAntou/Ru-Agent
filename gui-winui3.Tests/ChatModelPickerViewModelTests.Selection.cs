using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuApp.Tests;

public sealed partial class ChatModelPickerViewModelTests
{
    [Fact]
    public async Task Disposed_selection_cannot_authorize_a_new_submission_snapshot()
    {
        var (viewModel, _, _, _) = NewViewModel();
        await viewModel.LoadAsync();
        Assert.True(viewModel.TryCaptureRunSelection(out _));
        viewModel.Dispose();
        Assert.False(viewModel.TryCaptureRunSelection(out var selection));
        Assert.Null(selection);
    }

    [Fact]
    public async Task Settings_read_started_before_manual_choice_cannot_replace_it()
    {
        var (viewModel, backend, _, _) = NewViewModel();
        var oldRead = new TaskCompletionSource<AppSettingsDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.SettingsResponse = oldRead.Task;
        var load = viewModel.LoadAsync();

        await viewModel.SelectModelAsync("new-model", "models.dev:anthropic");
        oldRead.SetResult(new AppSettingsDto { BackendChoice = Backend, Model = Model });
        await load;

        Assert.Equal("new-model", viewModel.Model);
        Assert.Equal("models.dev:anthropic", viewModel.Backend);
        Assert.True(viewModel.TryCaptureRunSelection(out var runSelection));
        Assert.Equal(new CreateRunProviderSelection("models.dev:anthropic", "new-model"), runSelection);
    }

    [Fact]
    public async Task Settings_read_started_during_pending_save_cannot_replace_successful_choice()
    {
        var (viewModel, backend, _, _) = NewViewModel();
        var save = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRead = new TaskCompletionSource<AppSettingsDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.ModelSaveResponse = save.Task;
        var selection = viewModel.SelectModelAsync("new-model", "models.dev:anthropic");
        backend.SettingsResponse = oldRead.Task;
        var load = viewModel.LoadAsync();

        save.SetResult(true);
        await selection;
        oldRead.SetResult(new AppSettingsDto { BackendChoice = Backend, Model = Model });
        await load;

        Assert.Equal("new-model", viewModel.Model);
        Assert.Equal("models.dev:anthropic", viewModel.Backend);
    }

    [Fact]
    public async Task Queued_authoritative_grouped_selection_fences_an_older_settings_read()
    {
        var backend = new FakeBackend();
        var catalog = new FakeCatalog();
        var grouped = new FakeGrouped();
        var queuedDispatch = new List<Action>();
        var viewModel = new ChatModelPickerViewModel(queuedDispatch.Add, (_, _) => Task.CompletedTask);
        viewModel.Attach(backend, catalog, grouped);
        var oldRead = new TaskCompletionSource<AppSettingsDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.SettingsResponse = oldRead.Task;
        var load = viewModel.LoadAsync();

        grouped.SetSelection("models.dev:anthropic", "settings-model");
        oldRead.SetResult(new AppSettingsDto { BackendChoice = Backend, Model = Model });
        await load;

        // The authoritative signal is queued, but its version changed synchronously.
        // The old settings result must not overwrite it before the UI callback runs.
        Assert.Equal(string.Empty, viewModel.Model);
        Assert.Equal(("models.dev:anthropic", "settings-model"), grouped.Selection);
        foreach (var action in queuedDispatch.ToArray()) action();

        Assert.Equal("settings-model", viewModel.Model);
        Assert.Equal("models.dev:anthropic", viewModel.Backend);
    }

    [Fact]
    public async Task Fresh_read_after_queued_grouped_selection_is_allowed_and_manual_choice_survives_dispatch()
    {
        var backend = new FakeBackend();
        var grouped = new FakeGrouped();
        var queuedDispatch = new List<Action>();
        var viewModel = new ChatModelPickerViewModel(queuedDispatch.Add, (_, _) => Task.CompletedTask);
        viewModel.Attach(backend, new FakeCatalog(), grouped);

        grouped.SetSelection("models.dev:anthropic", "shell-model");
        await viewModel.SelectModelAsync("manual-model", "models.dev:openai");
        backend.Settings.BackendChoice = "models.dev:openai";
        backend.Settings.Model = "manual-model";

        await viewModel.LoadAsync();
        Assert.Same(backend.Settings, viewModel.CurrentSettings);
        Assert.Equal("manual-model", viewModel.Model);

        foreach (var action in queuedDispatch.ToArray()) action();

        Assert.Equal("manual-model", viewModel.Model);
        Assert.Equal("models.dev:openai", viewModel.Backend);
    }

    [Fact]
    public async Task Only_the_newest_settings_read_can_adopt_cross_client_settings()
    {
        var (viewModel, backend, _, _) = NewViewModel();
        var first = new TaskCompletionSource<AppSettingsDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AppSettingsDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.SettingsResponses.Enqueue(first.Task);
        backend.SettingsResponses.Enqueue(second.Task);
        var oldLoad = viewModel.LoadAsync();
        var newLoad = viewModel.LoadAsync();

        second.SetResult(new AppSettingsDto { BackendChoice = "models.dev:anthropic", Model = "cross-client-model" });
        await newLoad;
        first.SetResult(new AppSettingsDto { BackendChoice = Backend, Model = Model });
        await oldLoad;

        Assert.Equal("cross-client-model", viewModel.Model);
        Assert.Equal("models.dev:anthropic", viewModel.Backend);
    }

    [Fact]
    public async Task Grouped_catalog_selection_does_not_replace_the_visible_choice()
    {
        var (viewModel, _, _, grouped) = NewViewModel();
        await viewModel.LoadAsync();

        grouped.Publish("cross-client-model");
        viewModel.ApplyGroupedState(grouped.State);

        Assert.Equal(Model, viewModel.Model);
        Assert.Equal(Backend, viewModel.Backend);
        Assert.True(viewModel.TryCaptureRunSelection(out var selection));
        Assert.Equal(new CreateRunProviderSelection(Backend, Model), selection);
    }

    [Fact]
    public async Task Versioned_grouped_settings_selection_is_adopted_and_later_catalog_callbacks_cannot_revert_it()
    {
        var (viewModel, _, _, grouped) = NewViewModel();
        await viewModel.LoadAsync();

        grouped.SetSelection("models.dev:anthropic", "settings-model");
        Assert.Equal("settings-model", viewModel.Model);
        Assert.Equal("models.dev:anthropic", viewModel.Backend);

        grouped.Publish(Model);
        viewModel.ApplyGroupedState(grouped.State);

        Assert.Equal("settings-model", viewModel.Model);
        Assert.Equal("models.dev:anthropic", viewModel.Backend);
        Assert.True(viewModel.TryCaptureRunSelection(out var selection));
        Assert.Equal(new CreateRunProviderSelection("models.dev:anthropic", "settings-model"), selection);
    }

    [Fact]
    public async Task Settings_read_from_previous_binding_is_ignored_after_rebind_or_dispose()
    {
        var (viewModel, backend, _, _) = NewViewModel();
        var oldRead = new TaskCompletionSource<AppSettingsDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.SettingsResponse = oldRead.Task;
        var load = viewModel.LoadAsync();
        var replacement = new FakeBackend();
        viewModel.Attach(replacement, new FakeCatalog(), new FakeGrouped());
        Assert.False(viewModel.TryCaptureRunSelection(out _));
        oldRead.SetResult(new AppSettingsDto { BackendChoice = "stale", Model = "stale" });
        await load;

        Assert.Equal(string.Empty, viewModel.Model);
        Assert.Equal(string.Empty, viewModel.Backend);
        viewModel.Dispose();

        var (disposedViewModel, disposedBackend, _, _) = NewViewModel();
        var disposedRead = new TaskCompletionSource<AppSettingsDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        disposedBackend.SettingsResponse = disposedRead.Task;
        var disposedLoad = disposedViewModel.LoadAsync();
        disposedViewModel.Dispose();
        disposedRead.SetResult(new AppSettingsDto { BackendChoice = Backend, Model = Model });
        await disposedLoad;
        Assert.Equal(string.Empty, disposedViewModel.Model);
    }

    [Fact]
    public async Task Run_selection_is_unavailable_until_settings_load_and_when_choice_is_empty()
    {
        var (viewModel, backend, _, _) = NewViewModel();
        Assert.False(viewModel.TryCaptureRunSelection(out _));

        backend.Settings.Model = string.Empty;
        await viewModel.LoadAsync();
        Assert.False(viewModel.TryCaptureRunSelection(out _));
    }
}
