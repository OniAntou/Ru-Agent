namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        /// <summary>
        /// Wires one conversation controller independently of its input/output adapter.
        /// </summary>
        private void ConfigureChatEngine(Chat.ChatSessionEngine loop)
        {
            chatEngine = loop;
            loop.SetTaskCommandHandler((argument, token) => ShowTasksAsync(argument, token));
            loop.SetChangesCommandHandler((argument, token) => ShowChangesAsync(argument, token));
            loop.SetRewindCommandHandler((argument, token) => RewindHistoryAsync(argument, token));
            loop.SetSubmissionGuard(RewindSubmissionGuard);
            loop.SetHistoryStateProviders(() => pendingHistoryBranch is not null,
                line => historyReloadRequired && line.Trim().Equals("/sessions id:" + sessionId, StringComparison.OrdinalIgnoreCase));
            loop.SetShutdownSettlement(() => Task.WhenAll(
                runSession.WaitForRunSettlementAsync(), attachmentTasks.StopAsync()));
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
            loop.SetSubmissionHandler(async (submission, ct) =>
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
                    // Exit belongs to the conversation controller; the native host also
                    // stops its application and the workers for its other session pages.
                    if (multiSessions is not null) multiSessions.Stop(dispatcherExitCode.Value);
                    else { loop.Stop(); if (tgMode) tgApp!.Invoke(() => tgApp.RequestStop()); }
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
                // A quoted/rooted existing path is attachment input rather than prose.
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
            Task RunAttachmentAsync(Func<Task> action) => attachmentTasks.RunAsync(async () =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await action().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    await WriteDiagnosticLineAsync(_err, $"attachment_error: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
                }
            });

            if (tgMode)
            {
                tgWindow!.Composer.SetMeta(cmd.Preset, activeModel, backendName);
                tgWindow.Composer.SetReasoningEffort(activeReasoningEffort);
            }
            loop.RefreshAttachmentLabel();
        }

    }
}
