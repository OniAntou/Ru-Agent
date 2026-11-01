using System.Text.Json;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private (string Run, AgentUsageEstimateModel Estimate)? preparedContext;

        private Task<string?> ChooseStatusSectionAsync(CancellationToken ct) =>
            TgPanelRequests.RequestChoiceAsync(tgWindow!, new ChoiceList("status", [
                new("Overview", "Session, model and permission", "overview"),
                new("Context", "Latest request, system prompt, tools and messages", "context"),
                new("Session usage", "Recent run tokens, cache and estimated cost", "usage"),
            ], footer: "enter opens · esc closes · no model call"), ct);

        private async Task ShowUsageReportAsync(string section, CancellationToken ct)
        {
            var owner = sessionId;
            var run = runSession.ActiveRunId ?? runSession.ParkedRunId ?? runSession.FollowableRunId;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var reading = await new ChatUsageReader(_agentClient, _sseClient, _settingsClient.GetModelDetailsAsync)
                    .ReadAsync(owner, run, section == "context", deadline.Token).ConfigureAwait(false);
                IReadOnlyList<string> lines;
                if (section == "context")
                {
                    var row = reading.Runs.LastOrDefault();
                    var prepared = row is not null && preparedContext is { } saved && saved.Run == row.Run.RunId
                        && !row.ContextInvalidated && row.LatestContext is null && row.Reports.Count == 0 && row.Notice == "No provider usage report."
                        ? saved.Estimate : null;
                    var draft = await ReadDraftEstimateAsync(deadline.Token).ConfigureAwait(false);
                    lines = ChatUsagePresentation.Context(reading, prepared, usage.ContextLimit, draft, activeModel);
                }
                else lines = ChatUsagePresentation.Usage(reading);
                if (cmd.Json)
                    await _out.WriteLineAsync(JsonSerializer.Serialize(new { v = 1, type = section == "context" ? "context" : "session_usage",
                        session_id = owner, lines, data = reading })).ConfigureAwait(false);
                else foreach (var line in lines) await humanOut.WriteLineAsync(line).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                await WriteDiagnosticLineAsync(_err, "usage_timeout: could not finish reading usage within 20 seconds.").ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var code = error is AgentApiException api ? ChatDisplayText.Sanitize(api.ErrorCode, 96) : "usage_unavailable";
                await WriteDiagnosticLineAsync(_err, code + ": could not read usage; no totals were inferred.").ConfigureAwait(false);
            }
        }

        private async Task<long> ReadDraftEstimateAsync(CancellationToken ct)
        {
            long Estimate(string text) => ChatUsagePresentation.DraftEstimate(text,
                submissions.Texts.Sum(item => (long)item.TextContent.Length), submissions.Images.Count);
            if (!tgMode) return Estimate(string.Empty);
            var captured = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            tgApp!.Invoke(() => captured.TrySetResult(Estimate(tgWindow!.Composer.Input.Text)));
            return await captured.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
