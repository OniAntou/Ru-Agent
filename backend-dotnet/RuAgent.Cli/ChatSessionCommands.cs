using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

internal sealed class ChatSessionCommands
{
    private readonly Func<bool> _isBusy;
    private readonly Func<string> _currentSessionId;
    private readonly Func<CancellationToken, Task<string>> _createSession;
    private readonly Func<string?, CancellationToken, Task<IReadOnlyList<ChatSessionChoice>>> _readChoices;
    private readonly Func<string, Task> _switchSession;
    private readonly Func<ChoiceList, CancellationToken, Task<string?>>? _picker;
    private readonly Func<ChoiceList, CancellationToken, Task<SessionPickerOutcome>>? _sessionPicker;
    private readonly Func<string, CancellationToken, Task<SessionDeleteResult>>? _deleteSession;
    private readonly Func<TextWriter, string, Task> _writeLine;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly bool _json;

    public ChatSessionCommands(
        Func<bool> isBusy,
        Func<string> currentSessionId,
        Func<CancellationToken, Task<string>> createSession,
        Func<string?, CancellationToken, Task<IReadOnlyList<ChatSessionChoice>>> readChoices,
        Func<string, Task> switchSession,
        Func<ChoiceList, CancellationToken, Task<string?>>? picker,
        Func<ChoiceList, CancellationToken, Task<SessionPickerOutcome>>? sessionPicker,
        Func<string, CancellationToken, Task<SessionDeleteResult>>? deleteSession,
        Func<TextWriter, string, Task> writeLine,
        TextWriter output,
        TextWriter error,
        bool json)
    {
        _isBusy = isBusy;
        _currentSessionId = currentSessionId;
        _createSession = createSession;
        _readChoices = readChoices;
        _switchSession = switchSession;
        _picker = picker;
        _sessionPicker = sessionPicker;
        _deleteSession = deleteSession;
        _writeLine = writeLine;
        _output = output;
        _error = error;
        _json = json;
    }

    public async Task<bool> TryHandleAsync(string command, string? argument, CancellationToken cancellationToken)
    {
        if (command is not ("/new" or "/sessions" or "/resume"))
        {
            return false;
        }

        if (command is "/new" or "/resume" && _isBusy())
        {
            await _writeLine(_error, "session_busy: finish or cancel the active run before changing sessions").ConfigureAwait(false);
            return true;
        }

        if (command == "/sessions")
        {
            // The card picker owns the interactive path; the sequential engine (and --json)
            // keeps the plain list, because it has no panel to draw.
            if (_sessionPicker is null)
            {
                await ListSessionsAsync(argument, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await PickSessionAsync(argument, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        if (command == "/new")
        {
            await CreateAndSwitchAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var targetSessionId = argument?.Trim();
        if (!string.IsNullOrWhiteSpace(targetSessionId))
        {
            await _switchSession(targetSessionId).ConfigureAwait(false);
            return true;
        }

        if (_sessionPicker is not null)
        {
            await PickSessionAsync(null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (_json || _picker is null)
        {
            await _writeLine(_error, "interactive_required: /resume needs a session id or an interactive picker").ConfigureAwait(false);
            return true;
        }

        var choices = await ReadChoicesOrEmptyAsync(null, cancellationToken).ConfigureAwait(false);
        if (choices.Count == 0)
        {
            await _writeLine(_output, "no resumable sessions").ConfigureAwait(false);
            return true;
        }

        targetSessionId = await _picker(
            ChatSessionWorkflow.ToChoiceList(choices, _currentSessionId()),
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(targetSessionId))
        {
            return true;
        }

        await _switchSession(targetSessionId).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The interactive session card: pick to switch, Delete to remove the row under the
    /// cursor behind a confirm card, Escape to close. A deleted session reloads the list, and
    /// deleting the open session moves to another one — or starts a fresh one — matching the
    /// desktop client.
    /// </summary>
    private async Task PickSessionAsync(string? query, CancellationToken cancellationToken)
    {
        while (true)
        {
            var choices = await ReadChoicesOrEmptyAsync(query, cancellationToken).ConfigureAwait(false);
            if (choices.Count == 0)
            {
                await _writeLine(_output, "no resumable sessions").ConfigureAwait(false);
                return;
            }

            SessionPickerOutcome outcome;
            try
            {
                outcome = await _sessionPicker!(
                    ChatSessionWorkflow.ToChoiceList(choices, _currentSessionId()),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _writeLine(_error, $"session_picker_failed: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
                return;
            }

            if (outcome.SessionId is not { Length: > 0 } targetId)
            {
                return;
            }

            if (!outcome.DeleteRequested)
            {
                if (_isBusy())
                {
                    await _writeLine(_error, "session_busy: finish or cancel the active run before changing sessions").ConfigureAwait(false);
                    return;
                }

                await _switchSession(targetId).ConfigureAwait(false);
                return;
            }

            if (_isBusy())
            {
                await _writeLine(_error, "session_busy: finish or cancel the active run before deleting a session").ConfigureAwait(false);
                return;
            }

            if (!await ConfirmDeleteAsync(choices, targetId, cancellationToken).ConfigureAwait(false))
            {
                // The operator kept the session: the card comes back on the next round.
                continue;
            }

            var deleted = await DeleteSessionAsync(targetId, choices, cancellationToken).ConfigureAwait(false);
            if (!deleted.Deleted)
            {
                // The reloaded list shows whether the session survived; the error is
                // already in the transcript.
                continue;
            }

            if (string.Equals(targetId, _currentSessionId(), StringComparison.Ordinal))
            {
                await MoveOffDeletedSessionAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            // A fresh, unfiltered list for the next delete.
            query = null;
        }
    }

    /// <summary>
    /// The confirm card in front of a delete: the destructive entry is never the default, so
    /// a bare Enter keeps the session.
    /// </summary>
    private async Task<bool> ConfirmDeleteAsync(
        IReadOnlyList<ChatSessionChoice> choices,
        string targetId,
        CancellationToken cancellationToken)
    {
        var title = choices.FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal)) is { } choice
            && !string.IsNullOrWhiteSpace(choice.Title)
                ? choice.Title
                : targetId;
        var confirm = new ChoiceList(
            $"delete \"{title}\"?",
            [
                new ChoiceItem("1 · cancel", "keep this session", "cancel"),
                new ChoiceItem("2 · delete", "remove it permanently; this cannot be undone", "delete"),
            ],
            selected: 0,
            footer: "↑↓ select · enter apply · esc cancel");
        try
        {
            var answer = await _picker!(confirm, cancellationToken).ConfigureAwait(false);
            return string.Equals(answer, "delete", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _writeLine(_error, $"session_delete_failed: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
            return false;
        }
    }

    private async Task<SessionDeleteResult> DeleteSessionAsync(
        string targetId,
        IReadOnlyList<ChatSessionChoice> choices,
        CancellationToken cancellationToken)
    {
        var title = choices.FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal)) is { } choice
            && !string.IsNullOrWhiteSpace(choice.Title)
                ? choice.Title
                : targetId;
        try
        {
            var result = await _deleteSession!(targetId, cancellationToken).ConfigureAwait(false);
            if (result.Deleted)
            {
                var suffix = string.IsNullOrWhiteSpace(result.Message) ? string.Empty : $" · {result.Message}";
                await _writeLine(_output, $"session deleted · {title}{suffix}").ConfigureAwait(false);
            }
            else
            {
                await _writeLine(_error, $"session_delete_failed: {result.Message ?? "unknown error"}").ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _writeLine(_error, $"session_delete_failed: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
            return new SessionDeleteResult(false, null);
        }
    }

    /// <summary>
    /// The open session was just deleted: move to the newest remaining one, or start a fresh
    /// session when none is left. Nothing else is left to switch to, so the picker closes.
    /// </summary>
    private async Task MoveOffDeletedSessionAsync(CancellationToken cancellationToken)
    {
        var remaining = await ReadChoicesOrEmptyAsync(null, cancellationToken).ConfigureAwait(false);
        var next = remaining.FirstOrDefault();
        try
        {
            if (next is not null)
            {
                await _switchSession(next.Id).ConfigureAwait(false);
                return;
            }

            var created = await _createSession(cancellationToken).ConfigureAwait(false);
            await _switchSession(created).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _writeLine(_error, $"session_move_failed: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
        }
    }

    private async Task CreateAndSwitchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = await _createSession(cancellationToken).ConfigureAwait(false);
            await _switchSession(sessionId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _writeLine(_error, $"session_create_failed: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
        }
    }

    private async Task ListSessionsAsync(string? query, CancellationToken cancellationToken)
    {
        try
        {
            var choices = await _readChoices(query, cancellationToken).ConfigureAwait(false);
            if (choices.Count == 0)
            {
                await _writeLine(_output, "no sessions match").ConfigureAwait(false);
                return;
            }

            await _writeLine(
                _output,
                $"Sessions (showing up to {ChatSessionWorkflow.MaxListedSessions}; use /resume <id> to switch):").ConfigureAwait(false);
            foreach (var choice in choices)
            {
                var current = string.Equals(choice.Id, _currentSessionId(), StringComparison.Ordinal) ? "  (current)" : string.Empty;
                await _writeLine(_output, $"  {choice.Display}{current}").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _writeLine(_error, $"session_list_failed: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<ChatSessionChoice>> ReadChoicesOrEmptyAsync(string? query, CancellationToken cancellationToken)
    {
        try
        {
            return await _readChoices(query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _writeLine(_error, $"session_list_failed: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
            return [];
        }
    }
}
