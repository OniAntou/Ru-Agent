using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace RuApp.Services;

/// <summary>
/// Serializes every app ContentDialog and absorbs the two WinUI single-dialog
/// races: a second dialog while one is already open, and a new dialog while the
/// previous one's close animation is still running. Both surface as
/// "An async operation was not properly started. Only a single ContentDialog can
/// be open at any time.", which the workspace-trust path reported as a start
/// failure even though the run was parked and healthy.
/// </summary>
public static class ContentDialogGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const int ConflictRetryCount = 8;
    private static readonly TimeSpan ConflictRetryDelay = TimeSpan.FromMilliseconds(125);

    /// <summary>
    /// Shows one dialog, waiting for any other app dialog to close first and
    /// retrying briefly when WinUI is still dismissing the previous one. The
    /// wait resumes on the caller's thread, which must be the UI thread:
    /// <see cref="ContentDialog.ShowAsync"/> requires it.
    /// </summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        await Gate.WaitAsync();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                // A queued reader may have lost its owning message/session while
                // another dialog was open. Recheck on the UI thread before showing.
                if (isCurrent?.Invoke() == false) return ContentDialogResult.None;
                try
                {
                    return await dialog.ShowAsync();
                }
                catch (Exception exception) when (attempt < ConflictRetryCount && IsDialogConflict(exception))
                {
                    // The gate rules out a competing app dialog, so the remaining
                    // conflict is the previous dialog's closing animation: a
                    // short bounded wait lets it finish instead of failing the
                    // caller with a message that reads like a start failure.
                    await Task.Delay(ConflictRetryDelay);
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// True when the failure is the WinUI single-dialog condition rather than a
    /// real problem with the dialog's content or root.
    /// </summary>
    internal static bool IsDialogConflict(Exception exception) =>
        exception is COMException
        || exception.Message.Contains("Only a single ContentDialog", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("async operation was not properly started", StringComparison.OrdinalIgnoreCase);
}
