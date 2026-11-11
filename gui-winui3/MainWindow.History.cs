using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using RuApp.Services;

namespace RuApp
{
    /// <summary>
    /// Back and forward through the screens and sessions the reader has visited. Every
    /// way of getting somewhere records itself (the shell navigation and the chat's
    /// session-opened event); a move through the history replays the same paths, so it
    /// must not record itself again.
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly NavigationHistory<NavLocation> _history = new();
        private bool _applyingHistory;

        /// <summary>Notes a visit unless the history itself is moving; updates the buttons.</summary>
        private void RecordLocation(ShellDestination destination, string? sessionId)
        {
            if (_applyingHistory) return;
            _history.Record(new NavLocation(
                destination,
                destination == ShellDestination.Chat && !string.IsNullOrWhiteSpace(sessionId) ? sessionId : null));
            UpdateHistoryButtons();
        }

        /// <summary>
        /// The app opens on the chat without announcing it, so the first move away would
        /// leave nothing to go back to. Called before the chat is left, while its session
        /// is still known.
        /// </summary>
        private void SeedHistoryWithTheChatBeingLeft()
        {
            if (_history.HasCurrent || _applyingHistory) return;
            RecordLocation(ShellDestination.Chat, _chatView?.CurrentSessionId);
        }

        private void UpdateHistoryButtons()
        {
            if (HistoryBackButton is null || HistoryForwardButton is null) return;
            HistoryBackButton.IsEnabled = _history.CanGoBack;
            HistoryForwardButton.IsEnabled = _history.CanGoForward;
        }

        private async void OnHistoryBackClick(object sender, RoutedEventArgs e) => await MoveInHistoryAsync(-1);

        private async void OnHistoryForwardClick(object sender, RoutedEventArgs e) => await MoveInHistoryAsync(1);

        /// <summary>
        /// Any screen can be gone back to. A session can when the sidebar still lists it or
        /// the chat still holds it: an empty draft is held by the chat but not listed.
        /// </summary>
        private bool CanReturnTo(NavLocation location) =>
            location.SessionId is null
            || string.Equals(location.SessionId, _chatView?.CurrentSessionId, StringComparison.Ordinal)
            || Sessions.Any(session => string.Equals(session.Id, location.SessionId, StringComparison.Ordinal));

        private async Task MoveInHistoryAsync(int direction)
        {
            if (_applyingHistory || _closed) return;
            if (!_history.TryMove(direction, CanReturnTo, out var target))
            {
                UpdateHistoryButtons();
                return;
            }

            _applyingHistory = true;
            try
            {
                if (Shell.IsSettingsOpen) CloseSettingsPopup();
                if (target.Destination == ShellDestination.Chat)
                {
                    if (target.SessionId is null
                        || string.Equals(target.SessionId, _chatView?.CurrentSessionId, StringComparison.Ordinal))
                    {
                        OnNavClick(NavChat, new RoutedEventArgs());
                    }
                    else if (Sessions.FirstOrDefault(session => string.Equals(session.Id, target.SessionId, StringComparison.Ordinal)) is { } session)
                    {
                        await OpenSessionFromPaletteAsync(session);
                    }
                }
                else if (target.Destination == ShellDestination.Customize)
                {
                    OnNavClick(NavCustomize, new RoutedEventArgs());
                }
                else
                {
                    ApplyNavigation(Shell.Navigate(ShellRoutePolicy.TagFor(target.Destination)));
                }
            }
            catch (Exception exception)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] History move failed: {exception.Message}");
                SetSessionActionError("Could not go there");
            }
            finally
            {
                _applyingHistory = false;
                UpdateHistoryButtons();
            }
        }
    }
}
