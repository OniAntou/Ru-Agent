using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Cli.Attachments;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;


namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        /// <summary>
        /// The TG engine's entry point: the window is already built and no sequential
        /// surface fights Terminal.Gui. Slash lines and prompts go through the very same
        /// local handlers the sequential loop calls, with their writers redirected to
        /// the transcript.
        /// </summary>
        private Task RunTerminalGuiChatAsync() => new TuiSessionManager(this).RunAsync();

        private TgChatLoop CreateTerminalGuiLoop()
        {
            var loop = new TgChatLoop(tgWindow!, cancellationToken);
            if (multiSessions is not null) loop.SetSessionCommandHandler(line => multiSessions.TryHandleCommand(this, line));
            tgLoop = loop;
            loop.SetTaskCommandHandler((argument, token) => ShowTasksAsync(argument, token));
            loop.SetChangesCommandHandler((argument, token) => ShowChangesAsync(argument, token));
            loop.SetRewindCommandHandler((argument, token) => RewindHistoryAsync(argument, token));
            loop.SetSubmissionGuard(RewindSubmissionGuard);
            loop.SetHistoryStateProviders(() => pendingHistoryBranch is not null,
                line => historyReloadRequired && line.Trim().Equals("/sessions id:" + sessionId, StringComparison.OrdinalIgnoreCase));
            loop.SetShutdownSettlement(runSession.WaitForRunSettlementAsync);
            loop.SetSubmissionSnapshotFactory(line => historyReloadRequired && line.TrimStart().Split(' ', 2)[0].Equals("/sessions", StringComparison.OrdinalIgnoreCase)
                ? ChatSubmissionSnapshot.Create(line, sessionId, canonicalWorkspace, cmd.Preset, backendName ?? string.Empty, activeModel, [], [])
                : submissions.CreateSnapshot(
                line, sessionId, canonicalWorkspace, cmd.Preset,
                backendName ?? string.Empty, activeModel, runSession.ParkedRunId is not null));
            loop.SetSubmissionOwnershipHandlers(
                snapshot =>
                {
                    submissions.Admit(snapshot);
                    RefreshAttachmentLabels();
                },
                snapshot => ChatSubmissionState.Release(snapshot.Images));
            loop.SetStateChangingCommandClassifier(IsStateChangingSlashCommand);
            loop.SetSubmissionHandler(async (submission, _, ct) =>
            {
                if (submission.Prompt.TrimStart().StartsWith('/'))
                {
                    await HandleSlashLineWithDocumentAsync(submission.Prompt, ct).ConfigureAwait(false);
                }
                else
                {
                    await HandlePromptLineAsync(submission.Prompt, ct, submission).ConfigureAwait(false);
                }
                if (dispatcherExitCode is not null)
                {
                    // /exit and the fatal prompt paths record an exit code; the TG frame
                    // has no natural end, so stop both the loop and the app.
                    if (multiSessions is not null) multiSessions.Stop(dispatcherExitCode.Value);
                    else { loop.Stop(); tgApp!.Invoke(() => tgApp.RequestStop()); }
                }
            });

            // Attachment mutations refresh the hint row through RefreshAttachmentLabels;
            // these seams let the loop ask for the label/count and claim Ctrl+V
            // clipboard pastes and dropped paths without parsing anything itself.
            loop.SetAttachmentLabelProvider(CurrentAttachmentLabel);
            loop.SetAttachmentCountProvider(() => submissions.Count);
            loop.SetImageCountProvider(() => pendingImages.Count, () => submissions.Retryable?.Images.Length ?? 0);
            loop.SetPasteHandler(() => RunAttachmentAsync(ClipboardAttachAsync));
            loop.SetCopyHandler(Images.CliClipboardTextWriter.TryWriteAsync);
            loop.SetDropHandler(line =>
            {
                // Same shape as the classic idle loop: a quoted/rooted path that exists
                // on disk attaches instead of being sent as prose.
                var candidate = line.Trim().Trim('"', '\'');
                if (string.IsNullOrEmpty(candidate) || !IsDroppedAttachmentPath(candidate))
                {
                    return false;
                }

                _ = RunAttachmentAsync(() => AttachFileAsync(candidate));
                return true;
            });

            // The drop attach starts its own task from a synchronous handler; failures
            // that slip past AttachFileAsync's own error rows land in the transcript.
            async Task RunAttachmentAsync(Func<Task> action)
            {
                Interlocked.Increment(ref attachmentOperations);
                try
                {
                    await action().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await WriteDiagnosticLineAsync(_err, $"attachment_error: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
                }
                finally { Interlocked.Decrement(ref attachmentOperations); }
            }

            tgWindow!.Composer.SetMeta(cmd.Preset, activeModel, backendName);
            tgWindow.Composer.SetReasoningEffort(activeReasoningEffort);
            loop.RefreshAttachmentLabel();
            return loop;
        }

        private void TickSession(double elapsedSeconds)
        {
            tgWindow!.TickWelcome();
            sessionRenderer.Width = ChatRenderWidth();
            var transcriptWidth = tgWindow.Transcript.Viewport.Width;
            if (transcriptWidth > 0)
            {
                var state = sessionRenderer.Status.State;
                if (state is Tui.TuiRunState.Idle or Tui.TuiRunState.Failed)
                {
                    tgWindow.Transcript.ClearLive();
                }
                else
                {
                    // The live row is the running form of the settled tool row that
                    // will replace it: the timer owns its phase, the writer clears
                    // it the moment the permanent row lands.
                    tgWindow.Transcript.UpdateLive(sessionRenderer.PresentActivity(transcriptWidth));
                }
            }

            tgWindow.Hints.SetWaveClock(elapsedSeconds);
        }
    }
}
