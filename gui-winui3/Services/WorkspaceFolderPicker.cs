namespace RuApp.Services;

/// <summary>Owns one workspace folder selection operation.</summary>
internal sealed class WorkspaceFolderPicker(Func<Task<string?>> pickFolder)
{
    private readonly Func<Task<string?>> _pickFolder = pickFolder ?? throw new ArgumentNullException(nameof(pickFolder));
    private int _active;

    internal static WorkspaceFolderPicker For(Microsoft.UI.WindowId windowId) => new(async () =>
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId)
        {
            CommitButtonText = "Select Folder",
        };
        return (await picker.PickSingleFolderAsync())?.Path;
    });

    internal async Task<string?> PickAsync()
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0) return null;
        try
        {
            return await _pickFolder();
        }
        catch (OperationCanceledException) { return null; }
        catch (System.Runtime.InteropServices.COMException error)
            when (error.HResult is unchecked((int)0x800704C7) or unchecked((int)0x80004004))
        {
            // Native Cancel/close is a selection outcome, not a workspace failure.
            return null;
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }
}
