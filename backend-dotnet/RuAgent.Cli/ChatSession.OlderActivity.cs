using System.Collections.Immutable;
using RuAgent.Client;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    /// <summary>
    /// A reopened long run replays its newest steps under an "earlier steps" line. In the native
    /// terminal, scrolling that line into view reads the run's next older page and redraws the
    /// replay in place, keeping the reader's distance from the bottom so the older steps arrive
    /// above what they were reading. No command is involved. Once a prompt or navigation follows
    /// the replay, a redraw would erase newer rows, so loading stops and the line stays as it is.
    /// </summary>
    private sealed partial class ChatSession
    {
        private ImmutableArray<ChatReplayTurn>? replayedTurns;
        private string? replayedSessionId;
        private GetAgentRunResponse? replayedRetained;
        // The enrichment generation the native replay was drawn under; any admission or
        // navigation moves the generation on, after which a redraw would erase newer rows.
        private int? replayedGeneration;
        private bool olderStepsWired;
        private int olderStepsLoading;

        private void RememberReplayedTurns(string targetSessionId, ImmutableArray<ChatReplayTurn> turns,
            GetAgentRunResponse? retained, int generation)
        {
            replayedTurns = turns;
            replayedSessionId = targetSessionId;
            replayedRetained = retained;
            replayedGeneration = generation;
            if (!olderStepsWired && tgWindow is not null)
            {
                tgWindow.Transcript.ViewportMoved += OnTranscriptViewportMoved;
                olderStepsWired = true;
            }
        }

        // UI thread: the transcript raises this after every viewport move.
        private void OnTranscriptViewportMoved()
        {
            if (cmd.Json || replayedTurns is not { } turns || replayedGeneration is not { } drawn
                || drawn != Volatile.Read(ref enrichmentGeneration)
                || !string.Equals(replayedSessionId, sessionId, StringComparison.Ordinal)
                || tgWindow!.Transcript.FirstVisibleMatchOrdinal(ChatSessionTurnReplay.IsOlderStepsNotice) is not { } ordinal)
            {
                return;
            }

            var candidates = ChatSessionTurnReplay.TurnsWithOlderSteps(turns);
            if (ordinal >= candidates.Length || Interlocked.Exchange(ref olderStepsLoading, 1) == 1) return;
            var index = candidates[ordinal];
            _ = Task.Run(async () =>
            {
                try
                {
                    await LoadOlderStepsAsync(index, drawn).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref olderStepsLoading, 0);
                }
            });
        }

        private async Task LoadOlderStepsAsync(int index, int drawn)
        {
            if (replayedTurns is not { } turns || index >= turns.Length
                || turns[index] is not { RunId: { } runId, Activity: { NextBeforeSequence: { } before } shown } turn)
            {
                return;
            }

            AgentRunActivityReplay page;
            try
            {
                page = await _agentClient.ListRunActivityReplayAsync(runId, before, limit: null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                TuiDebug.Log("history", $"older steps unavailable: {ex.GetType().Name}");
                return;
            }

            // A prompt or navigation that came in meanwhile owns the transcript now.
            if (drawn != Volatile.Read(ref enrichmentGeneration)) return;
            turns = turns.SetItem(index, turn with { Activity = ChatSessionTurnReplay.MergeOlder(shown, page) });
            replayedTurns = turns;
            RedrawReplayedTurns(turns, replayedRetained, drawn);
        }

        /// <summary>
        /// Replaces the native replay with the widened turns in one UI batch, keeping the reader's
        /// distance from the bottom: the older steps land above what was on screen.
        /// </summary>
        private void RedrawReplayedTurns(ImmutableArray<ChatReplayTurn> turns, GetAgentRunResponse? retained, int generation)
        {
            var batch = new List<Action>();
            var transcript = tgWindow!.Transcript;
            var writer = new TgTranscriptWriter(tgApp!, transcript, tgWindow.SetTodos, batch.Add);
            var renderer = CreateSessionRenderer(writer);
            renderer.Width = sessionRenderer.Width;
            RenderSessionTurns(turns, renderer, prompt => SessionHistoryPromptWriter.Write(prompt, true,
                typed => batch.Add(() => new TgChatEngineView(tgWindow).EchoPrompt(typed, false)),
                renderer.Surface.WriteLine));
            if (retained is not null) renderer.Note(RetainedRunNotice(retained),
                IsParkedRunStatus(retained.Status) ? TuiToken.Warn : TuiToken.Meta);
            RunOnTgUi(() =>
            {
                if (generation != Volatile.Read(ref enrichmentGeneration)) return;
                var rowsBelow = transcript.RowsBelowViewport;
                transcript.DeferWrapping = true;
                try
                {
                    transcript.Clear();
                    foreach (var apply in batch) apply();
                }
                finally
                {
                    transcript.DeferWrapping = false;
                    if (rowsBelow > 0) transcript.ScrollRows(-rowsBelow);
                }
            });
        }
    }
}
