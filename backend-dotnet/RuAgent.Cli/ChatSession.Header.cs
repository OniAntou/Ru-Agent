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
        int ChatRenderWidth() => tgMode && tgWindow!.Transcript.Viewport.Width > 0
            ? tgWindow.Transcript.Viewport.Width : Tui.TuiViewport.Width();

        // Best effort: the header's percent and cost need the catalog's published context
        // window and rates; a catalog that cannot answer leaves both off rather than
        // inventing numbers.
        Task RefreshModelUsageInfoAsync(
            CancellationToken ct,
            string? retainedBackend = null,
            string? retainedModel = null,
            long? retainedContextWindow = null,
            string? retainedProtocol = null,
            bool isRetainedRun = false) =>
            modelUsage.RefreshAsync(usage, new ChatModelUsageSelection(
                isRetainedRun ? retainedBackend : backendName,
                isRetainedRun ? retainedModel : activeModel,
                isRetainedRun, retainedContextWindow, retainedProtocol), tgMode, ct);

        async Task ShowRetainedRunProviderAsync(GetAgentRunResponse run)
        {
            if (!tgMode)
            {
                return;
            }

            var retainedBackend = string.IsNullOrWhiteSpace(run.ProviderBackend) ? "(provider unknown)" : run.ProviderBackend;
            var retainedModel = string.IsNullOrWhiteSpace(run.ProviderModelLabel)
                ? string.IsNullOrWhiteSpace(run.ProviderModel) ? "(model unknown)" : run.ProviderModel
                : run.ProviderModelLabel;
            var retainedProvider = string.Join(" · ", new[]
            {
                retainedBackend,
                string.IsNullOrWhiteSpace(run.ProviderProtocol) ? "protocol unknown" : run.ProviderProtocol,
                string.IsNullOrWhiteSpace(run.ProviderCallingMode) ? "mode unknown" : run.ProviderCallingMode,
            });
            RunOnTgUi(() =>
            {
                tgWindow!.Composer.SetMeta(run.Preset, retainedModel, retainedProvider);
                // Retained run summaries do not expose their pinned reasoning level.
                tgWindow.Composer.SetReasoningEffort(null);
            });
            await RefreshModelUsageInfoAsync(
                cancellationToken,
                run.ProviderBackend,
                run.ProviderModel,
                run.ContextWindowTokens,
                run.ProviderProtocol,
                isRetainedRun: true).ConfigureAwait(false);
        }

        // Best effort and bounded: a fresh session may not have a title until the host has
        // seen a message, so the header keeps the plain caption until one arrives. The probe
        // only runs where the header exists and has its own short HTTP deadline.
        async Task RefreshSessionTitleAsync()
        {
            if (!tgMode || string.IsNullOrEmpty(sessionId) || !string.IsNullOrWhiteSpace(sessionTitle) || sessionTitleProbes >= 3)
            {
                return;
            }

            sessionTitleProbes++;
            try
            {
                var title = await TgSessionTitleReader.ReadAsync(
                    token => _sessionClient.GetSessionAsync(sessionId, token), cancellationToken).ConfigureAwait(false);
                if (title is not null)
                {
                    sessionTitle = title;
                    RunOnTgUi(() => tgWindow!.Header.SetTitle(sessionTitle));
                }
            }
            catch (OperationCanceledException)
            {
                // The enclosing turn was cancelled: the probe must not swallow the cleanup
                // and the server-side run cancel that follow it.
            }
            catch (Exception ex)
            {
                // The header keeps its caption when the title cannot be read.
                Tui.TuiDebug.Log("header", $"session title read failed: {CliErrorText.ShortMessage(ex.Message)}");
            }
        }

        // Every diagnostic line the REPL prints goes through here so --json keeps stdout
        // pure and TG mode lands the line in its transcript; sequential writes go to the
        // requested writer unchanged.
        Task WriteDiagnosticLineAsync(TextWriter writer, string line) =>
            ResolveDiagnosticWriter(tgMode, tgWriter, writer).WriteLineAsync(line);

        /// <summary>Drops the header's live subagent strip; a new turn starts with none.</summary>
        void ClearSubagentStrip()
        {
            subagentTracker.Reset();
            if (tgMode)
            {
                RunOnTgUi(() => tgWindow!.Header.SetSubagents([]));
            }
        }

        /// <summary>
        /// Runs one TG view mutation on the Terminal.Gui loop. The frame/probe paths resume
        /// on thread-pool continuations, so any direct view write from here would race the
        /// driver's draw; an already-disposed session drops the late mutation instead.
        /// </summary>
        void RunOnTgUi(Action action)
        {
            try
            {
                tgApp!.Invoke(action);
            }
            catch (NotInitializedException)
            {
                // The TG session wound down; late view mutations are dropped.
            }
        }

        async Task RefreshModelSelectionAsync()
        {
            try
            {
                var s = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
                var bName = s.TryGetProperty("backend", out var bProp) && !string.IsNullOrWhiteSpace(bProp.GetString())
                    ? bProp.GetString()!
                    : backendName;
                var curModel = bName switch
                {
                    "OpenAI" => s.TryGetProperty("openai_model", out var oam) ? oam.GetString() ?? "" : "",
                    "Anthropic" => s.TryGetProperty("anthropic_model", out var anm) ? anm.GetString() ?? "" : "",
                    _ => s.TryGetProperty("model", out var mProp) ? mProp.GetString() ?? "" : "",
                };

                var backendChanged = !string.IsNullOrWhiteSpace(bName)
                    && !string.Equals(bName, backendName, StringComparison.Ordinal);
                var modelChanged = !string.IsNullOrWhiteSpace(curModel)
                    && !string.Equals(curModel, activeModel, StringComparison.Ordinal);
                activeReasoningEffort = ReadReasoningEffort(s);
                if (backendChanged)
                {
                    backendName = bName;
                }

                if (modelChanged)
                {
                    // The composer meta row, the vision pre-check and the usage cluster read
                    // this local, so a /model switch is reflected without a restart.
                    activeModel = curModel;
                }

                if (backendChanged || modelChanged)
                {
                    await RefreshModelUsageInfoAsync(cancellationToken).ConfigureAwait(false);
                }
                multiSessions?.RememberModelDefaults(this);
            }
            catch (Exception)
            {
                // Settings refresh is best-effort; retain the last selected provider/model.

            }

            if (tgMode)
            {
                // A /model or /backend switch must land on the TG composer meta row too.
                RunOnTgUi(() =>
                {
                    tgWindow!.Composer.SetMeta(cmd.Preset, activeModel, backendName);
                    tgWindow.Composer.SetReasoningEffort(activeReasoningEffort);
                });
            }
        }
    }
}
