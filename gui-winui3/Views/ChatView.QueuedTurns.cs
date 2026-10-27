using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Controls;
using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using Windows.Storage.Streams;
using System.IO;

namespace RuApp.Views
{
    public sealed partial class ChatView
    {
        private void OnRemoveQueuedTurnClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is QueuedAgentTurn turn)
            {
                Composer.Queue.Remove(turn);
            }
        }

        private async void OnSendQueuedTurnsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                // A hand-driven flush answers this click, so the stop hold that was
                // guarding the automatic drain no longer applies to what is left.
                Composer.Queue.ReleaseHold();
                await DrainQueuedTurnsAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                ShowAgentError(exception);
            }
        }

        /// <summary>
        /// Hands the first queued turn over: called when a turn reaches its end, and
        /// by the strip's own Send now control.
        /// </summary>
        private async Task DrainQueuedTurnsAsync(CancellationToken cancellationToken)
        {
            if (_agentController is null) return;
            var sessionId = _activeSessionId;
            var generation = _agentTurnGeneration;
            try
            {
                await Composer.Queue.DrainAsync((next, accepted) => SendAgentTurnAsync(
                    ChatComposerViewModel.BuildEffectivePrompt(next.Text, next.Attachments), cancellationToken,
                    attachments: next.Attachments.ToList(), displayUserText: next.DisplayText,
                    onAccepted: accepted));
            }
            catch (Exception exception)
            {
                if (generation == _agentTurnGeneration && sessionId == _activeSessionId)
                {
                    FailActiveAgentReply(exception);
                    ShowAgentError(exception);
                }
            }
            finally { UpdateQueuedTurnsPanel(); }
        }

        private void UpdateQueuedTurnsPanel()
        {
            // Header text and Send now are bound; the strip itself animates in and out.
            Composer.SetRunInHand(_isSendButtonInStopMode || IsActiveAgentRunPending());
            if (QueuedTurnsPanel is not null)
            {
                Services.UiMotion.SetVisible(QueuedTurnsPanel, Composer.HasQueuedTurns);
            }
        }
    }
}
