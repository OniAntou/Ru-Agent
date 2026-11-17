using System.Text.Json;
using RuAgent.Cli.Tui;
using System.Globalization;
using System.Text;
using RuAgent.Client;

namespace RuAgent.Cli;

internal sealed record ChatSessionChoice(
    string Id,
    string Title,
    int MessageCount,
    string WorkspaceLabel,
    string? WorkspacePath,
    DateTimeOffset? UpdatedAt = null)
{
    public string Display
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title;
            var messages = MessageCount == 0 ? "empty" : $"{MessageCount} messages";
            var updated = SessionRelativeTime.Format(UpdatedAt);
            var activity = updated is null ? string.Empty : $" · {updated}";
            return $"{title} · {messages} · {WorkspaceLabel}{activity} · {Id}";
        }
    }
}

/// <summary>
/// The compact age shown beside a saved session. Relative copy keeps the card readable while
/// still telling the operator which conversation is recent; absolute dates would eat the
/// right-hand detail column.
/// </summary>
internal static class SessionRelativeTime
{
    public static string? Format(DateTimeOffset? value, DateTimeOffset? now = null)
    {
        if (value is not { } updated)
        {
            return null;
        }

        var delta = (now ?? DateTimeOffset.UtcNow) - updated;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)delta.TotalMinutes)}m ago";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)delta.TotalHours)}h ago";
        }

        if (delta < TimeSpan.FromDays(7))
        {
            return $"{Math.Max(1, (int)delta.TotalDays)}d ago";
        }

        return delta < TimeSpan.FromDays(365)
            ? updated.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture)
            : updated.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }
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

    /// <summary>
    /// The workspace a newly opened session runs in. The recorded workspace wins
    /// when it differs from the terminal's: a session created in Desktop (including a
    /// Ru-owned scratch workspace) must open in the folder its history belongs to
    /// instead of being refused. A recorded folder that no longer exists fails closed
    /// with a named error rather than silently opening elsewhere.
    /// </summary>
    public static (string Path, string Label) ResolveOpenWorkspace(
        string currentPath,
        string currentLabel,
        (string Path, string Label)? recorded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        if (recorded is not { } workspace || string.IsNullOrWhiteSpace(workspace.Path))
        {
            return (currentPath, currentLabel);
        }

        var recordedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Path));
        var currentFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPath));
        if (string.Equals(recordedPath, currentFull, StringComparison.OrdinalIgnoreCase))
        {
            return (currentPath, currentLabel);
        }

        if (!Directory.Exists(recordedPath))
        {
            throw new InvalidOperationException(
                "session_workspace_missing: the session's recorded workspace no longer exists");
        }

        var label = string.IsNullOrWhiteSpace(workspace.Label)
            ? ChatDisplayText.Sanitize(Path.GetFileName(recordedPath), 64)
            : workspace.Label;
        return (recordedPath, label);
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
                && !choice.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                && !choice.WorkspaceLabel.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                && (choice.WorkspacePath?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) != true))
            {
                continue;
            }

            choices.Add(choice);
        }

        return choices;
    }

    public static ChoiceList ToChoiceList(IReadOnlyList<ChatSessionChoice> sessions, string? currentSessionId)
    {
        // The card shows the title left and the three facts that tell two same-titled
        // conversations apart on the right: how much they hold, how old they are and which
        // workspace they belong to. The current one carries a bullet so it stays visible when
        // the filter moves the selection elsewhere.
        var items = sessions.Select(session =>
        {
            var title = string.IsNullOrWhiteSpace(session.Title) ? "Untitled" : session.Title;
            var current = string.Equals(session.Id, currentSessionId, StringComparison.Ordinal);
            var messages = session.MessageCount == 0 ? "empty" : $"{session.MessageCount} msg";
            var updated = SessionRelativeTime.Format(session.UpdatedAt);
            var detail = updated is null
                ? $"{messages} · {session.WorkspaceLabel}"
                : $"{messages} · {updated} · {session.WorkspaceLabel}";
            return new ChoiceItem(
                current ? "● " + title : title,
                detail,
                session.Id);
        }).ToArray();
        var selected = Array.FindIndex(items, item => string.Equals(item.Value, currentSessionId, StringComparison.Ordinal));
        return new ChoiceList(
            "sessions",
            items,
            selected < 0 ? 0 : selected,
            "↑↓ select · enter switch · del delete · type searches · esc close");
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

        DateTimeOffset? updatedAt = null;
        if (item.TryGetProperty("updated_at", out var updatedElement)
            && updatedElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(updatedElement.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsedUpdated))
        {
            updatedAt = parsedUpdated;
        }

        choice = new ChatSessionChoice(id!, title, messageCount, workspaceLabel.Length == 0 ? "workspace unknown" : workspaceLabel, workspacePath, updatedAt);
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
