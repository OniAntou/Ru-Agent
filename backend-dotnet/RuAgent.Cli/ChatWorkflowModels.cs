using System.Text.Json;
using RuAgent.Cli.Tui;
using System.Globalization;
using System.Text;
using RuAgent.Client;

namespace RuAgent.Cli;

internal sealed record ChatSessionChoice(string Id, string Title, int MessageCount, string WorkspaceLabel, string? WorkspacePath)
{
    public string Display =>
        $"{(string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title)} · {MessageCount} messages · {WorkspaceLabel} · {Id}";
}

/// <summary>
/// What the session picker resolved: the chosen session, a delete request for the row under
/// the cursor, or neither (Escape closed the card).
/// </summary>
internal readonly record struct SessionPickerOutcome(string? SessionId, bool DeleteRequested);

/// <summary>
/// The outcome of one session delete. <see cref="Deleted"/> stays true when only the
/// session's artifacts remain: the chat itself is gone and a retry is the host's own advice.
/// </summary>
internal readonly record struct SessionDeleteResult(bool Deleted, string? Message)
{
    /// <summary>
    /// The host's bounded message for a partial cleanup. The body carries no machine code for
    /// it, so this sentence is what tells that state apart from an undeleted session (the
    /// desktop client matches the same string).
    /// </summary>
    public const string PartialArtifactsMessage = "Session đã xoá nhưng artifacts còn sót, hãy thử xoá lại.";
}

internal static class ChatSessionWorkflow
{
    public const int MaxListedSessions = 50;
    private const int MaxScannedSessions = 500;
    private const int MaxQueryChars = 128;
    private const int MaxTitleChars = 120;
    private const int MaxWorkspacePathChars = 1024;

    public static async Task<(string Path, string Label)?> ReadWorkspaceLabelAsync(
        SessionApiClient sessionClient,
        string targetSessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var choices = Filter(await sessionClient.ListAsync(cancellationToken).ConfigureAwait(false), null);
            var choice = choices.FirstOrDefault(item => string.Equals(item.Id, targetSessionId, StringComparison.Ordinal));
            if (choice?.WorkspacePath is not { } path)
            {
                return null;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            var safePath = ChatDisplayText.Sanitize(fullPath, 512);
            return safePath.Length == 0 || safePath.Length > 512
                ? null
                : (fullPath, choice.WorkspaceLabel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static IReadOnlyList<ChatSessionChoice> Filter(JsonElement root, string? query)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("sessions", out var sessions)
            || sessions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var normalizedQuery = query?.Trim();
        if (normalizedQuery?.Length > MaxQueryChars)
        {
            normalizedQuery = normalizedQuery[..MaxQueryChars];
        }

        var choices = new List<ChatSessionChoice>(MaxListedSessions);
        var scanned = 0;
        foreach (var item in sessions.EnumerateArray())
        {
            if (scanned++ >= MaxScannedSessions || choices.Count >= MaxListedSessions)
            {
                break;
            }

            if (!TryRead(item, out var choice))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(normalizedQuery)
                && !choice.Id.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                && !choice.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            choices.Add(choice);
        }

        return choices;
    }

    public static ChoiceList ToChoiceList(IReadOnlyList<ChatSessionChoice> sessions, string? currentSessionId)
    {
        // The card shows the title left and the identity right: a session is told apart by
        // its title plus how much it holds, not by a 60-character one-line dump.
        var items = sessions.Select(session => new ChoiceItem(
            string.Equals(session.Id, currentSessionId, StringComparison.Ordinal)
                ? $"{(string.IsNullOrWhiteSpace(session.Title) ? "Untitled" : session.Title)} (current)"
                : string.IsNullOrWhiteSpace(session.Title) ? "Untitled" : session.Title,
            $"{session.MessageCount} msg · {session.WorkspaceLabel}",
            session.Id)).ToArray();
        var selected = Array.FindIndex(items, item => string.Equals(item.Value, currentSessionId, StringComparison.Ordinal));
        return new ChoiceList(
            "sessions",
            items,
            selected < 0 ? 0 : selected,
            "↑↓ select · enter switch · del delete · esc close");
    }

    private static bool TryRead(JsonElement item, out ChatSessionChoice choice)
    {
        choice = new ChatSessionChoice(string.Empty, string.Empty, 0, string.Empty, null);
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var id = idElement.GetString()?.Trim();
        if (!IsValidSessionId(id))
        {
            return false;
        }

        var title = item.TryGetProperty("title", out var titleElement)
            && titleElement.ValueKind == JsonValueKind.String
                ? ChatDisplayText.Sanitize(titleElement.GetString(), MaxTitleChars)
                : string.Empty;

        var messageCount = item.TryGetProperty("message_count", out var countElement)
            && countElement.ValueKind == JsonValueKind.Number
            && countElement.TryGetInt32(out var count)
                ? Math.Max(0, count)
                : 0;

        var workspacePath = item.TryGetProperty("workspace_path", out var pathElement)
            && pathElement.ValueKind == JsonValueKind.String
                ? pathElement.GetString()?.Trim()
                : null;
        if (workspacePath?.Length > MaxWorkspacePathChars)
        {
            workspacePath = null;
        }

        var workspaceLabel = item.TryGetProperty("workspace_label", out var labelElement)
            && labelElement.ValueKind == JsonValueKind.String
                ? ChatDisplayText.Sanitize(labelElement.GetString(), 64)
                : string.Empty;
        if (workspaceLabel.Length == 0 && !string.IsNullOrWhiteSpace(workspacePath))
        {
            workspaceLabel = ChatDisplayText.Sanitize(Tui.TuiHeader.ShortenWorkspace(workspacePath, 64), 64);
        }

        choice = new ChatSessionChoice(id!, title, messageCount, workspaceLabel.Length == 0 ? "workspace unknown" : workspaceLabel, workspacePath);
        return true;
    }

    public static bool IsValidSessionId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return false;
        }

        return value.All(character => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_' or '-');
    }
}

internal static class ChatDisplayText
{
    public static string Sanitize(string? value, int maxChars)
    {
        var stripped = TuiText.Strip(value);
        var result = new StringBuilder(Math.Min(stripped.Length, maxChars));
        var pendingSpace = false;
        foreach (var rune in stripped.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            if (result.Length + rune.Utf16SequenceLength > maxChars)
            {
                return result.Length == 0 ? string.Empty : result.ToString().TrimEnd() + "…";
            }

            result.Append(rune);
        }

        return result.ToString().TrimEnd();
    }
}
