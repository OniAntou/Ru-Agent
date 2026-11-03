using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed partial class ChatModelPickerViewModelTests
{
    [Fact]
    public async Task Selected_catalog_starts_without_waiting_for_the_provider_directory()
    {
        var (viewModel, backend, catalog, _) = NewViewModel();
        using (viewModel)
        {
            var directory = new TaskCompletionSource<IReadOnlyList<ModelsDevEntryDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.ModelsDevResponse = directory.Task;
            var load = viewModel.LoadAsync();
            try
            {
                Assert.Equal(Backend, catalog.Backend);
                Assert.Equal(Model, viewModel.ModelLabel);
            }
            finally
            {
                directory.TrySetResult([]);
                await load;
            }
        }
    }

    [Fact]
    public async Task The_first_open_uses_the_selected_catalog_while_grouped_catalogs_are_still_empty()
    {
        var (viewModel, _, _, _) = NewViewModel();
        using (viewModel)
        {
            await viewModel.LoadAsync();
            var projections = new List<ProviderModelPickerProjection>();
            viewModel.PickerProjectionReady += projections.Add;
            viewModel.OnPickerOpened();
            Assert.Contains(projections.SelectMany(item => item.Groups).SelectMany(item => item.Rows),
                row => row.ModelId == Model && row.IsSelected && row.IsEnabled);
        }
    }

    [Fact]
    public async Task An_open_empty_picker_updates_as_soon_as_the_selected_catalog_arrives()
    {
        var (viewModel, backend, catalog, _) = NewViewModel();
        using (viewModel)
        {
            await viewModel.LoadAsync();
            catalog.State = new();
            var directory = new TaskCompletionSource<IReadOnlyList<ModelsDevEntryDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.ModelsDevResponse = directory.Task;
            var projections = new List<ProviderModelPickerProjection>();
            viewModel.PickerProjectionReady += projections.Add;
            viewModel.OnPickerOpened();
            Assert.Equal("Loading models…", projections.Last().EmptyStateText);
            catalog.State = ReadyCatalog(Backend);
            catalog.Raise(catalog.State);
            Assert.Contains(projections.Last().Groups.SelectMany(item => item.Rows), row => row.ModelId == Model);
            directory.SetResult([]);
        }
    }

    [Fact]
    public async Task Continuous_provider_updates_share_one_publish_deadline_instead_of_postponing_the_first_paint()
    {
        var deadlines = new List<TaskCompletionSource>();
        var (viewModel, _, _, grouped) = NewViewModel((_, token) =>
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            deadlines.Add(pending);
            return pending.Task.WaitAsync(token);
        });
        using (viewModel)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.PickerProjectionReady += _ => changed.TrySetResult();
            viewModel.OnPickerOpened();
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            grouped.Publish("first");
            var firstDeadline = deadlines[0];
            grouped.SetSelection(Backend, "second");
            grouped.Publish("second");
            firstDeadline.TrySetResult();
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("second", viewModel.ModelLabel);
        }
    }

    [Theory]
    [InlineData("ok", null, false)]
    [InlineData("error", "catalog_unauthorized", false)]
    [InlineData("error", null, true)]
    public async Task Selected_catalog_only_fills_a_group_that_has_not_received_a_result(
        string status, string? errorCode, bool showsSelected)
    {
        var (viewModel, _, _, grouped) = NewViewModel();
        using (viewModel)
        {
            await viewModel.LoadAsync();
            grouped.CurrentDefinitions = [new(Backend, Backend, "OpenAI", 0)];
            var projections = new List<ProviderModelPickerProjection>();
            viewModel.PickerProjectionReady += projections.Add;
            viewModel.OnPickerOpened();
            grouped.PublishCatalog(new() { Backend = Backend, Status = status, ErrorCode = errorCode });
            Assert.Equal(showsSelected, projections.Last().Groups.SelectMany(item => item.Rows).Any(row => row.ModelId == Model));
        }
    }
}
