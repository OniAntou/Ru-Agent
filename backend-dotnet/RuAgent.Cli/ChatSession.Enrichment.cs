using RuAgent.Client;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private readonly object enrichmentGate = new();
        private readonly ChatAttachmentTasks enrichmentTasks = new();
        private CancellationTokenSource? enrichmentCancellation;
        private int enrichmentGeneration;

        private int CancelSessionEnrichment()
        {
            lock (enrichmentGate)
            {
                Interlocked.Increment(ref enrichmentGeneration);
                enrichmentCancellation?.Cancel();
                enrichmentCancellation = null;
                return enrichmentGeneration;
            }
        }

        private async Task StopSessionEnrichmentAsync()
        {
            CancelSessionEnrichment();
            try { await enrichmentTasks.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { TuiDebug.Log("history", "optional session reads did not settle before shutdown"); }
        }

        private async Task<IReadOnlyList<GetAgentRunResponse>> ReadSessionSummariesAsync(string targetSessionId)
        {
            try { return await _agentClient.ListSessionRunsAsync(targetSessionId, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                TuiDebug.Log("history", $"session summaries unavailable: {ex.GetType().Name}");
                return [];
            }
        }

        // Only the list/classification owns readiness. Optional reads have a separate
        // generation, invalidated before any accepted prompt is echoed or state changes.
        private void StartSessionEnrichment(string targetSessionId, ChatSessionReplayResult replay,
            IReadOnlyList<GetAgentRunResponse> runs, GetAgentRunResponse? retained, int generation)
        {
            CancellationTokenSource source;
            lock (enrichmentGate)
            {
                // A prompt may have been queued while mandatory classification was
                // pending. Its admission must also prevent enrichment from starting.
                if (generation != enrichmentGeneration || cancellationToken.IsCancellationRequested) return;
                source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                source.CancelAfter(TimeSpan.FromSeconds(30));
                enrichmentCancellation = source;
            }
            var ct = source.Token;
            var originalUsage = usage;
            var width = sessionRenderer.Width;
            var selection = retained is null ? new ChatModelUsageSelection(backendName, activeModel)
                : new ChatModelUsageSelection(retained.ProviderBackend, retained.ProviderModel, true,
                    retained.ContextWindowTokens, retained.ProviderProtocol);
            bool IsCurrent() => !ct.IsCancellationRequested
                && generation == Volatile.Read(ref enrichmentGeneration);

            _ = enrichmentTasks.RunAsync(() => Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(RestoreHistoryAsync(), RestoreUsageAsync()).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex) { TuiDebug.Log("history", $"optional session reads unavailable: {ex.GetType().Name}"); }
                finally
                {
                    lock (enrichmentGate)
                    {
                        if (ReferenceEquals(enrichmentCancellation, source)) enrichmentCancellation = null;
                        source.Dispose();
                    }
                }
            }));

            async Task RestoreHistoryAsync()
            {
                if (cmd.Json) return;
                var turnsTask = replay.Messages.Length > 0
                    ? ChatSessionTurnReplay.LoadAsync(_sessionClient, _agentClient, targetSessionId, ct, runs)
                    : Task.FromResult<System.Collections.Immutable.ImmutableArray<ChatReplayTurn>?>(null);
                var todosTask = retained is null ? Task.FromResult<AgentRunActivityReplay?>(null)
                    : ReadRetainedTodosAsync(retained.RunId, ct);
                await Task.WhenAll(turnsTask, todosTask).ConfigureAwait(false);
                if (!IsCurrent()) return;

                // Build native operations off the UI thread, then commit the whole replay
                // atomically. Per-row Invoke calls must not race the next prompt's echo.
                var batch = new List<Action>();
                var transcript = tgWindow!.Transcript;
                var writer = new TgTranscriptWriter(tgApp!, transcript, tgWindow.SetTodos, batch.Add);
                var renderer = CreateSessionRenderer(writer);
                renderer.Width = width;
                var turns = await turnsTask.ConfigureAwait(false);
                if (turns is { Length: > 0 } loaded)
                {
                    RenderSessionTurns(loaded, renderer, prompt => SessionHistoryPromptWriter.Write(prompt, true,
                        typed => batch.Add(() => new TgChatEngineView(tgWindow).EchoPrompt(typed, false)),
                        renderer.Surface.WriteLine));
                    if (retained is not null) renderer.Note(RetainedRunNotice(retained),
                        IsParkedRunStatus(retained.Status) ? TuiToken.Warn : TuiToken.Meta);
                }
                if (await todosTask.ConfigureAwait(false) is { } todos) renderer.Transcript.RestoreTodos(todos);
                RunOnTgUi(() =>
                {
                    if (!IsCurrent()) return;
                    if (turns is { Length: > 0 } drawn) RememberReplayedTurns(targetSessionId, drawn, retained, generation);
                    var rowsBelow = transcript.RowsBelowViewport;
                    transcript.DeferWrapping = true;
                    try
                    {
                        if (turns is { Length: > 0 }) transcript.Clear();
                        foreach (var apply in batch) apply();
                    }
                    finally
                    {
                        transcript.DeferWrapping = false;
                        if (rowsBelow > 0) transcript.ScrollRows(-rowsBelow);
                    }
                });
            }

            async Task RestoreUsageAsync()
            {
                var restored = new TuiUsageTracker();
                await modelUsage.RefreshAsync(restored, selection, true, ct).ConfigureAwait(false);
                await RestoreSessionUsageAsync(targetSessionId, runs, restored, ct).ConfigureAwait(false);
                if (replay.Messages.LastOrDefault() is { Role: not "user" } last) restored.ObserveReplyTail(last.Text.Length);
                RunOnTgUi(() =>
                {
                    if (!IsCurrent() || !ReferenceEquals(usage, originalUsage)) return;
                    usage = restored;
                    PaintContextUsage(IsCurrent);
                });
            }
        }

        private async Task<AgentRunActivityReplay?> ReadRetainedTodosAsync(string runId, CancellationToken ct)
        {
            try { return await _agentClient.ListRunActivityReplayAsync(runId, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                TuiDebug.Log("history", $"retained todos unavailable: {ex.GetType().Name}");
                return null;
            }
        }

        private static string RetainedRunNotice(GetAgentRunResponse run) => IsParkedRunStatus(run.Status)
            ? TuiTranscript.FormatParkedNotice(run.RunId)
            : run.Status.ToLowerInvariant() switch
            {
                "waiting_input" => $"run {run.RunId} is waiting for an answer · type Continue",
                "waiting_approval" => $"run {run.RunId} is waiting for approval · type Continue",
                _ => $"run {run.RunId} is still running · attach with 'ru agent attach {run.RunId}' or type Continue here",
            };
    }
}
