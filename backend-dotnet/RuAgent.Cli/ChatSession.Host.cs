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
        private TextWriter _err => _host._err;
        private TextReader _in => _host._in;
        private SessionApiClient _sessionClient => _host._sessionClient;
        private AgentApiClient _agentClient => _host._agentClient;
        private AgentSseClient _sseClient => _host._sseClient;
        private SettingsApiClient _settingsClient => _host._settingsClient;
        private AgentSlashCommands _slashCommands => tgMode
            ? sessionSlashCommands ??= new AgentSlashCommands(_settingsClient, _sessionClient,
                tgWriter!, tgWriter!, _host._skillsClient)
            : _host._slashCommands;
        private RunStartCore _runStartCore => tgMode
            ? sessionRunStartCore ??= new RunStartCore(_sessionClient, _agentClient, tgWriter!, tgWriter!)
            : _host._runStartCore;

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
            _host.ShowSubagentsAsync(argument, currentRoot, output, json, ct);
    }
}
