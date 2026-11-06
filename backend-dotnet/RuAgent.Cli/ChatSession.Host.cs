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
        // The command object owns the clients and writers; the session reaches them through
        // these forwards so the extracted bodies read exactly like the closure they came from.
        private TextWriter _out => _host._out;
        private TextWriter _err => nativeCommandOutput?.Error ?? _host._err;
        private TextReader _in => _host._in;
        private SessionApiClient _sessionClient => _host._sessionClient;
        private AgentApiClient _agentClient => _host._agentClient;
        private AgentSseClient _sseClient => _host._sseClient;
        private SettingsApiClient _settingsClient => _host._settingsClient;
        private AgentSlashCommands _slashCommands => tgMode
            ? sessionSlashCommands ??= new AgentSlashCommands(_settingsClient, _sessionClient,
                humanOut, _err)
            : _host._slashCommands;
        private RunStartCore _runStartCore => tgMode
            ? sessionRunStartCore ??= new RunStartCore(_sessionClient, _agentClient, tgWriter!, tgWriter!)
            : _host._runStartCore;

        private async Task<JsonElement> ReadSubmissionSettingsAsync(CancellationToken ct)
        {
            var reconnect = _host.ReconnectHost;
            try
            {
                return await _settingsClient.GetAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException error) when (reconnect is not null && !ct.IsCancellationRequested
                && error.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded)
            {
                // This is a read before any session, preset or run write. Do not
                // apply transport recovery to requests that may have been accepted.
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            var session = await reconnect!(deadline.Token).ConfigureAwait(false);
            if (session == _host._session)
                return await _settingsClient.GetAsync(deadline.Token).ConfigureAwait(false);

            var replacement = new AgentCommands(session, _host._httpClient, _host._out, _host._err,
                _host._in, _host._sseHttpClient) { ReconnectHost = reconnect };
            JsonElement settings;
            try
            {
                settings = await replacement._settingsClient.GetAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
            }
            catch
            {
                replacement.Dispose();
                throw;
            }

            if (ownsReconnectedHost) _host.Dispose();
            _host = replacement;
            ownsReconnectedHost = true;
            sessionSlashCommands = null;
            sessionRunStartCore = null;
            inputWorkflow = null;
            ConfigureSlashCommands();
            return settings;
        }

        private Task<(string InputId, string Question, IReadOnlyList<string> Options)?> ReadInputRequestAsync(
            string runId, string? inputId, CancellationToken ct) =>
            _host.ReadInputRequestAsync(runId, inputId, ct);

        private Task WriteBusyConflictAsync(
            AgentApiException error,
            Func<TextWriter, string, Task> writeLineAsync,
            Action<string, string>? markBusy = null) =>
            _host.WriteBusyConflictAsync(error, writeLineAsync, markBusy);

        private Task StreamRunEventsResumedAsync(
            string runId,
            Func<SseFrame, CancellationToken, Task> onFrameAsync,
            CancellationToken ct,
            long afterSequence = 0,
            Func<SseFrame, bool>? stopWhen = null) =>
            _host.StreamRunEventsResumedAsync(runId, onFrameAsync, ct, afterSequence, stopWhen);

        private Task ShowSubagentsAsync(string? argument, string? currentRoot, TextWriter output, bool json, CancellationToken ct) =>
            _host.ShowSubagentsAsync(argument, currentRoot, output, json, _err, ct);
    }
}
