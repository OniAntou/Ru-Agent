using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using RuApp.Models;
using RuApp.Services;

namespace RuApp;

/// <summary>
/// Tells the user when a run finishes or waits on them while Ru is in the background:
/// a taskbar flash, a sound, and optionally a Windows notification. The open session is
/// covered live (finish, approval, question) through its run controller; other sessions
/// only report a finish, when the session list sees them stop running.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan ReplayQuietPeriod = TimeSpan.FromSeconds(3);
    private readonly AgentNotificationPolicy _notificationPolicy = new();
    private readonly NotificationPreferenceStore _notificationStore = new();
    private NotificationPreferences _notificationPreferences = NotificationPreferences.Default;
    private AgentRunController? _notifiedController;
    private DateTime _notificationQuietUntil;
    private bool _toastRegistered;

    internal NotificationPreferences NotificationPreferences => _notificationPreferences;

    private void InitializeNotifications()
    {
        _notificationPreferences = _notificationStore.Load();
        Sidebar.Sessions.CollectionChanged += OnNotifiedSessionsChanged;
        foreach (var session in Sidebar.Sessions) WatchSession(session);
        ApplyToastRegistration();
    }

    /// <summary>Saves new preferences and registers or unregisters Windows notifications to match.</summary>
    internal bool UpdateNotificationPreferences(NotificationPreferences preferences)
    {
        _notificationPreferences = preferences;
        ApplyToastRegistration();
        return _notificationStore.TrySave(preferences);
    }

    private void AttachNotifications(AgentRunController? controller)
    {
        if (_notifiedController is not null) _notifiedController.PropertyChanged -= OnNotifiedControllerChanged;
        _notifiedController = controller;
        _notificationQuietUntil = DateTime.UtcNow + ReplayQuietPeriod;
        if (controller is not null) controller.PropertyChanged += OnNotifiedControllerChanged;
    }

    private void ShutdownNotifications()
    {
        AttachNotifications(null);
        Sidebar.Sessions.CollectionChanged -= OnNotifiedSessionsChanged;
        foreach (var session in Sidebar.Sessions) session.PropertyChanged -= OnNotifiedSessionChanged;
        if (_toastRegistered) AppNotificationManager.Default.NotificationInvoked -= OnToastInvoked;
    }

    private void OnNotifiedControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed || sender is not AgentRunController controller || !ReferenceEquals(controller, _notifiedController)) return;
        var sessionId = _chatView?.CurrentSessionId;
        var title = Sidebar.Sessions.FirstOrDefault(session => session.Id == sessionId)?.Title;
        switch (e.PropertyName)
        {
            case nameof(AgentRunController.CurrentRun):
                var run = controller.CurrentRun;
                if (run is not null && !AgentNotificationPolicy.IsTerminal(run.Status) && IsNewRun(run.RunId))
                    _notificationQuietUntil = DateTime.UtcNow + ReplayQuietPeriod;
                Deliver(_notificationPolicy.ObserveRun(sessionId, title, run?.RunId, run?.Status));
                break;
            case nameof(AgentRunController.PendingApprovalId):
            case nameof(AgentRunController.PendingInputId):
                // Waits replayed while a session opens were already on screen when it opened.
                if (DateTime.UtcNow < _notificationQuietUntil)
                {
                    _notificationPolicy.MarkSeen(controller.PendingApprovalId, controller.PendingInputId);
                    return;
                }

                Deliver(_notificationPolicy.ObserveWait(sessionId, title, controller.PendingApprovalId, controller.PendingInputId));
                break;
        }
    }

    private string? _lastObservedRunId;

    private bool IsNewRun(string? runId)
    {
        bool isNew = !string.Equals(runId, _lastObservedRunId, StringComparison.Ordinal);
        _lastObservedRunId = runId;
        return isNew;
    }

    private void OnNotifiedSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var session in e.OldItems?.OfType<SessionInfo>() ?? []) session.PropertyChanged -= OnNotifiedSessionChanged;
        foreach (var session in e.NewItems?.OfType<SessionInfo>() ?? []) WatchSession(session);
    }

    private void WatchSession(SessionInfo session)
    {
        session.PropertyChanged -= OnNotifiedSessionChanged;
        session.PropertyChanged += OnNotifiedSessionChanged;
        _notificationPolicy.ObserveBackgroundSession(session.Id, session.Title, session.IsAgentRunning);
    }

    private void OnNotifiedSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed || e.PropertyName != nameof(SessionInfo.IsAgentRunning) || sender is not SessionInfo session) return;
        var notification = _notificationPolicy.ObserveBackgroundSession(session.Id, session.Title, session.IsAgentRunning);
        // The open session is reported live by its controller.
        if (!string.Equals(session.Id, _chatView?.CurrentSessionId, StringComparison.Ordinal)) Deliver(notification);
    }

    private void Deliver(AgentNotification? notification)
    {
        var preferences = _notificationPreferences;
        if (notification is null || _closed) return;
        bool wanted = notification.Kind is AgentNotificationKind.Finished or AgentNotificationKind.Stopped
            ? preferences.OnFinished
            : preferences.OnNeedsAttention;
        if (!wanted || IsRuInForeground()) return;

        if (preferences.FlashTaskbar) FlashTaskbar();
        bool toastShown = preferences.ShowToast && ShowToast(notification);
        // A Windows notification plays its own sound.
        if (preferences.PlaySound && !toastShown) MessageBeep(MbIconAsterisk);
    }

    private bool IsRuInForeground()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return GetForegroundWindow() == handle && !IsIconic(handle);
    }

    private void FlashTaskbar()
    {
        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = WinRT.Interop.WindowNative.GetWindowHandle(this),
            Flags = FlashwAll | FlashwTimerNoForeground,
        };
        FlashWindowEx(ref info);
    }

    private void ApplyToastRegistration()
    {
        try
        {
            if (_notificationPreferences.ShowToast && !_toastRegistered)
            {
                AppNotificationManager.Default.NotificationInvoked += OnToastInvoked;
                AppNotificationManager.Default.Register();
                _toastRegistered = true;
            }
            else if (!_notificationPreferences.ShowToast && _toastRegistered)
            {
                AppNotificationManager.Default.NotificationInvoked -= OnToastInvoked;
                AppNotificationManager.Default.Unregister();
                _toastRegistered = false;
            }
        }
        catch (Exception error)
        {
            // Without Windows notifications the flash and the sound still work.
            GuiDiagnosticWriter.AppendTraceLine($"[Notifications] Toast registration failed: {error.GetType().Name}");
        }
    }

    private bool ShowToast(AgentNotification notification)
    {
        if (!_toastRegistered) return false;
        try
        {
            var toast = new AppNotificationBuilder()
                .AddArgument("session", notification.SessionId)
                .AddText(notification.Title)
                .AddText(notification.Body)
                .BuildNotification();
            AppNotificationManager.Default.Show(toast);
            return true;
        }
        catch (Exception error)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Notifications] Toast failed: {error.GetType().Name}");
            return false;
        }
    }

    private void OnToastInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        args.Arguments.TryGetValue("session", out var sessionId);
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_closed) return;
            Activate();
            // Only a session Ru lists opens; anything else just brings the window forward.
            if (Sidebar.Sessions.FirstOrDefault(session => session.Id == sessionId) is { } target
                && !string.Equals(target.Id, _chatView?.CurrentSessionId, StringComparison.Ordinal))
                await OpenSessionFromPaletteAsync(target);
        });
    }

    private const uint FlashwAll = 0x3;
    private const uint FlashwTimerNoForeground = 0xC;
    private const uint MbIconAsterisk = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashWindowInfo info);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);
}
