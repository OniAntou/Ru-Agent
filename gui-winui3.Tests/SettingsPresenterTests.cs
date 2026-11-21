using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SettingsPresenterTests
{
    [Fact]
    public async Task A_late_response_from_an_older_settings_load_is_ignored()
    {
        var presenter = new SettingsPresenter();
        var oldLoad = new TaskCompletionSource<AppSettingsDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newest = new AppSettingsDto { CloudflareAccountId = "new" };
        long oldRequest = presenter.BeginLoad();
        Task<SettingsLoadResult?> first = presenter.LoadAsync(oldRequest, () => oldLoad.Task);
        long newRequest = presenter.BeginLoad();
        Task<SettingsLoadResult?> second = presenter.LoadAsync(newRequest, () => Task.FromResult<AppSettingsDto?>(newest));

        Assert.Equal("new", (await second)?.Settings.CloudflareAccountId);
        oldLoad.SetResult(new AppSettingsDto { CloudflareAccountId = "old" });

        Assert.Null(await first);
    }

    [Fact]
    public async Task A_load_that_becomes_stale_during_provider_preparation_is_not_applied()
    {
        var presenter = new SettingsPresenter();
        long oldRequest = presenter.BeginLoad();
        var preparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool fetched = false;
        Task<SettingsLoadResult?> oldLoad = presenter.LoadAsync(
            oldRequest,
            () => { fetched = true; return Task.FromResult<AppSettingsDto?>(new()); },
            () => preparation.Task);

        long newestRequest = presenter.BeginLoad();
        var newest = await presenter.LoadAsync(newestRequest, () => Task.FromResult<AppSettingsDto?>(new()));
        preparation.SetResult();

        Assert.True(fetched);
        Assert.Null(await oldLoad);
        Assert.NotNull(newest);
    }

    [Fact]
    public async Task Settings_save_uses_the_latest_pending_form_snapshot()
    {
        var presenter = new SettingsPresenter();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        string choice = "first";

        Task<bool> SaveAsync()
        {
            string snapshot = choice;
            seen.Add(snapshot);
            return snapshot == "first" ? SaveFirstAsync() : Task.FromResult(true);
        }

        async Task<bool> SaveFirstAsync()
        {
            await releaseFirst.Task;
            return true;
        }

        Task<bool> first = presenter.SaveAsync(SaveAsync);
        choice = "latest";
        Task<bool> queued = presenter.SaveAsync(SaveAsync);
        releaseFirst.SetResult();

        Assert.True(await first);
        Assert.True(await queued);
        Assert.Equal(new[] { "first", "latest" }, seen);
    }

    [Fact]
    public async Task A_failed_save_does_not_drop_a_newer_queued_snapshot()
    {
        var presenter = new SettingsPresenter();
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        Task<bool> first = presenter.SaveAsync(async () =>
        {
            seen.Add("first");
            await firstGate.Task;
            throw new InvalidOperationException("offline");
        });
        Task<bool> queued = presenter.SaveAsync(() =>
        {
            seen.Add("latest");
            return Task.FromResult(true);
        });

        firstGate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.True(await queued);
        Assert.Equal(new[] { "first", "latest" }, seen);
    }

    [Fact]
    public async Task Connection_test_waits_for_the_latest_coalesced_save_before_refreshing()
    {
        var presenter = new SettingsPresenter();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        Task<bool> first = presenter.SaveAsync(async () =>
        {
            order.Add("first-save-start");
            await releaseFirst.Task;
            order.Add("first-save-end");
            return true;
        });

        Task<ProviderConnectionTestRun> connection = SettingsProviderPresenter.TestConnectionAsync(
            async () =>
            {
                bool saved = await presenter.SaveAsync(() =>
                {
                    order.Add("latest-save");
                    return Task.FromResult(true);
                });
                Assert.True(saved);
                return saved;
            },
            () => { order.Add("refresh"); return Task.FromResult(true); },
            () => null);

        await Task.Yield();
        Assert.DoesNotContain("refresh", order);
        releaseFirst.SetResult();
        await Task.WhenAll(first, connection);

        Assert.Equal(new[] { "first-save-start", "first-save-end", "latest-save", "refresh" }, order);
    }

    [Fact]
    public async Task Connection_test_does_not_refresh_after_its_coalesced_save_fails()
    {
        var presenter = new SettingsPresenter();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = presenter.SaveAsync(async () =>
        {
            await releaseFirst.Task;
            return true;
        });
        bool refreshed = false;
        Task<ProviderConnectionTestRun> connection = SettingsProviderPresenter.TestConnectionAsync(
            () => presenter.SaveAsync(() => Task.FromResult(false)),
            () => { refreshed = true; return Task.FromResult(true); },
            () => null);

        releaseFirst.SetResult();
        await Task.WhenAll(first, connection);

        Assert.Null((await connection).State);
        Assert.False(refreshed);
    }

    [Fact]
    public void Autosave_is_coalesced_and_cancelled_when_a_settings_load_starts()
    {
        var presenter = new SettingsPresenter();

        Assert.False(presenter.ScheduleAutoSave(isLoading: true));
        Assert.True(presenter.ScheduleAutoSave(isLoading: false));
        Assert.True(presenter.ScheduleAutoSave(isLoading: false));
        Assert.NotEqual(0, presenter.BeginLoad());

        Assert.False(presenter.TryConsumeAutoSave());
        Assert.True(presenter.ScheduleAutoSave(isLoading: false));
        Assert.True(presenter.TryConsumeAutoSave());
        Assert.False(presenter.TryConsumeAutoSave());
    }
}
