namespace RuApp.Services;

/// <summary>
/// The half-typed message per conversation.
///
/// Switching sessions used to keep the draft in a private dictionary inside
/// ChatView with the save/restore interleaved into the session-switch path, so the
/// only way to know whether a draft survived a switch was to try it by hand. The
/// rule lives here now.
/// </summary>
public sealed class ComposerDraftStore
{
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);

    /// <summary>Number of sessions currently holding a draft.</summary>
    public int Count => _drafts.Count;

    /// <summary>
    /// Records what the user has typed for a session. A blank value drops the entry
    /// instead of storing it: an empty box is the absence of a draft, and keeping it
    /// would let a cleared box overwrite a draft held for the same session later.
    /// </summary>
    public void Save(string? sessionId, string? text)
    {
        if (!IsUsableSessionId(sessionId))
        {
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            _drafts.Remove(sessionId!);
            return;
        }

        _drafts[sessionId!] = text;
    }

    /// <summary>The draft for a session, or empty when the user has typed nothing.</summary>
    public string Restore(string? sessionId)
    {
        if (!IsUsableSessionId(sessionId))
        {
            return string.Empty;
        }

        return _drafts.TryGetValue(sessionId!, out var draft) ? draft : string.Empty;
    }

    public bool HasDraft(string? sessionId) =>
        IsUsableSessionId(sessionId) && _drafts.ContainsKey(sessionId!);

    /// <summary>Drops a session's draft, for example once the message has been sent.</summary>
    public void Clear(string? sessionId)
    {
        if (IsUsableSessionId(sessionId))
        {
            _drafts.Remove(sessionId!);
        }
    }

    /// <summary>
    /// Moves the draft from one session to another. Used when the backend replaces a
    /// stale session: the text the user is looking at belongs to the new id now.
    /// </summary>
    public void Move(string? fromSessionId, string? toSessionId)
    {
        if (string.IsNullOrEmpty(fromSessionId)
            || string.IsNullOrEmpty(toSessionId)
            || string.Equals(fromSessionId, toSessionId, StringComparison.Ordinal))
        {
            return;
        }

        if (_drafts.Remove(fromSessionId!, out var draft))
        {
            _drafts[toSessionId!] = draft;
        }
    }

    /// <summary>
    /// A session id has to be a real id; ChatView treats a missing or blank one as
    /// "no session yet", so keying a draft on it would strand the text.
    /// </summary>
    private static bool IsUsableSessionId(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId);
}
