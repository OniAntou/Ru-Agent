using System.Collections.Immutable;
using RuAgent.Client;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    /// <summary>
    /// A reopened long run replays its newest steps; /older reads the next older page of the
    /// newest turn that still has one. While the replay is still the whole native transcript the
    /// turns are redrawn in place; otherwise (a prompt was sent since, or line mode) the turn's
    /// fuller record is printed again below, so nothing already on screen is rewritten.
    /// </summary>
    private sealed partial class ChatSession
    {
        private ImmutableArray<ChatReplayTurn>? replayedTurns;
        private string? replayedSessionId;
        private GetAgentRunResponse? replayedRetained;
        // The enrichment generation the native replay was drawn under; any admission or
        // navigation moves the generation on, after which a redraw would erase newer rows.
        private int? replayedGeneration;

        private static bool IsOlderCommand(string? line) =>
            line?.TrimStart().Split(' ', 2)[0].Equals("/older", StringComparison.OrdinalIgnoreCase) == true;

        private void RememberReplayedTurns(string targetSessionId, ImmutableArray<ChatReplayTurn> turns,
            GetAgentRunResponse? retained, int? generation)
        {
            replayedTurns = turns;
            replayedSessionId = targetSessionId;
            replayedRetained = retained;
            replayedGeneration = generation;
        }

        private async Task LoadOlderActivityAsync(CancellationToken ct)
        {
            if (replayedTurns is not { } turns || !string.Equals(replayedSessionId, sessionId, StringComparison.Ordinal))
            {
                await ReportSessionNavigationAsync("no reopened turn has earlier steps").ConfigureAwait(false);
                return;
            }

            var index = -1;
            for (var candidate = turns.Length - 1; candidate >= 0; candidate--)
            {
                if (turns[candidate] is { RunId: not null, Activity.NextBeforeSequence: not null })
                {
                    index = candidate;
                    break;
                }
            }

            if (index < 0)
            {
                await ReportSessionNavigationAsync("every reopened turn already shows all of its steps").ConfigureAwait(false);
                return;
            }

            var turn = turns[index];
            AgentRunActivityReplay page;
            try
            {
                page = await _agentClient.ListRunActivityReplayAsync(
                    turn.RunId!, turn.Activity!.NextBeforeSequence, limit: null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                TuiDebug.Log("history", $"older activity unavailable: {ex.GetType().Name}");
                await ReportSessionNavigationAsync("could not load earlier steps · try /older again").ConfigureAwait(false);
                return;
            }

            var widened = turn with { Activity = ChatSessionTurnReplay.MergeOlder(turn.Activity!, page) };
            turns = turns.SetItem(index, widened);
            replayedTurns = turns;

            if (tgMode && !cmd.Json && replayedGeneration is { } drawn && drawn == Volatile.Read(ref enrichmentGeneration))
            {
                RedrawReplayedTurns(turns, replayedRetained, drawn);
                return;
            }

            sessionRenderer.Note("↑ earlier steps of a reopened turn", TuiToken.Meta);
            RenderSessionTurns([widened]);
        }

        /// <summary>Replaces the native replay with the widened turns in one UI batch.</summary>
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
                transcript.DeferWrapping = true;
                try
                {
                    transcript.Clear();
                    foreach (var apply in batch) apply();
                }
                finally
                {
                    transcript.DeferWrapping = false;
                }
            });
        }
    }
}
