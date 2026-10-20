using RuApp.Models;

namespace RuApp.Services;

internal sealed record SettingsLoadResult(long Request, AppSettingsDto Settings);

/// <summary>
/// Coordinates SettingsView data ownership without depending on WinUI. It makes
/// overlapping loads latest-wins and serializes autosaves to the newest form state.
/// </summary>
internal sealed class SettingsPresenter
{
    private readonly SettingsSaveQueue _saves = new();
    private long _loadRequest;
    private int _autoSavePending;

    internal long BeginLoad()
    {
        Interlocked.Exchange(ref _autoSavePending, 0);
        return Interlocked.Increment(ref _loadRequest);
    }

    internal bool IsCurrentLoad(long request) => request == Volatile.Read(ref _loadRequest);

    internal bool ScheduleAutoSave(bool isLoading)
    {
        if (isLoading) return false;
        Interlocked.Exchange(ref _autoSavePending, 1);
        return true;
    }

    internal bool TryConsumeAutoSave() => Interlocked.Exchange(ref _autoSavePending, 0) != 0;

    internal async Task<SettingsLoadResult?> LoadAsync(
        long request,
        Func<Task<AppSettingsDto?>> load,
        Func<Task>? prepareProviderState = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        var settings = await load().ConfigureAwait(true);
        if (!IsCurrentLoad(request)) return null;
        if (prepareProviderState is not null)
        {
            await prepareProviderState().ConfigureAwait(true);
            if (!IsCurrentLoad(request)) return null;
        }

        return new SettingsLoadResult(request, settings ?? new AppSettingsDto());
    }

    internal Task<bool> SaveAsync(Func<Task<bool>> save) => _saves.SaveAsync(save);
}
