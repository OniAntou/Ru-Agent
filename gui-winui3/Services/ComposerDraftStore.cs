using RuApp.Models;
using System.Text;

namespace RuApp.Services;

/// <summary>
/// The unsent composer content per conversation.
///
/// Switching sessions used to keep the draft in a private dictionary inside
/// ChatView with the save/restore interleaved into the session-switch path, so the
/// only way to know whether a draft survived a switch was to try it by hand. The
/// per-session rule lives here now.
/// </summary>
public sealed class ComposerDraftStore
{
    public const int MaxRetainedSessions = 8;
    public const int MaxRetainedBytes = 32 * 1024 * 1024;
    public const int MaxTextBytesPerSession = 1024 * 1024;
    public const int MaxAttachmentsPerSession = 8;
    public const int MaxAttachmentBytesPerSession = 24 * 1024 * 1024;
    public const string RetentionLimitStatus = "This draft exceeds the bounded session storage limit. Remove some text or attachments before switching sessions.";
    private const int AttachmentMetadataAllowanceBytes = 512;

    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ImageAttachmentItem>> _attachments = new(StringComparer.Ordinal);

    /// <summary>Number of sessions currently holding draft text or attachments.</summary>
    public int Count => _drafts.Keys.Concat(_attachments.Keys).Distinct(StringComparer.Ordinal).Count();

    /// <summary>
    /// Records what the user has typed for a session. A blank value drops the entry
    /// instead of storing it: an empty box is the absence of a draft, and keeping it
    /// would let a cleared box overwrite a draft held for the same session later.
    /// </summary>
    public bool Save(string? sessionId, string? text)
    {
        if (!IsUsableSessionId(sessionId))
        {
            return true;
        }

        return TrySave(sessionId, text, RestoreAttachments(sessionId));
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
        IsUsableSessionId(sessionId)
        && (_drafts.ContainsKey(sessionId!) || _attachments.ContainsKey(sessionId!));

    public bool CanSave(string? sessionId, string? text, IEnumerable<ImageAttachmentItem> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (!IsUsableSessionId(sessionId))
        {
            return true;
        }

        var snapshot = attachments.Take(MaxAttachmentsPerSession + 1).ToList();
        return snapshot.Count <= MaxAttachmentsPerSession
            && CanRetain(sessionId!, text, snapshot);
    }

    public bool TrySave(string? sessionId, string? text, IEnumerable<ImageAttachmentItem> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (!IsUsableSessionId(sessionId))
        {
            return true;
        }

        string sessionKey = sessionId!;
        var candidates = attachments.Take(MaxAttachmentsPerSession + 1).ToList();
        if (candidates.Count > MaxAttachmentsPerSession || !CanRetain(sessionKey, text, candidates))
        {
            return false;
        }

        var snapshot = candidates.Select(CloneAttachment).ToList();
        SaveText(sessionKey, text);
        if (snapshot.Count == 0)
        {
            _attachments.Remove(sessionKey);
        }
        else
        {
            _attachments[sessionKey] = snapshot;
        }

        return true;
    }

    public IReadOnlyList<ImageAttachmentItem> RestoreAttachments(string? sessionId) =>
        IsUsableSessionId(sessionId) && _attachments.TryGetValue(sessionId!, out var attachments)
            ? attachments.Select(CloneAttachment).ToList()
            : Array.Empty<ImageAttachmentItem>();

    /// <summary>Drops a session's draft, for example once the message has been sent.</summary>
    public void Clear(string? sessionId)
    {
        if (IsUsableSessionId(sessionId))
        {
            _drafts.Remove(sessionId!);
            _attachments.Remove(sessionId!);
        }
    }

    /// <summary>Moves all unsent composer content to a replacement session.</summary>
    public bool Move(string? fromSessionId, string? toSessionId)
    {
        if (string.IsNullOrEmpty(fromSessionId)
            || string.IsNullOrEmpty(toSessionId)
            || string.Equals(fromSessionId, toSessionId, StringComparison.Ordinal))
        {
            return true;
        }

        string draft = Restore(fromSessionId);
        var attachments = RestoreAttachments(fromSessionId);
        if (!TrySave(toSessionId, draft, attachments))
        {
            return false;
        }

        Clear(fromSessionId);
        return true;
    }

    private bool CanRetain(string sessionId, string? text, IReadOnlyList<ImageAttachmentItem> attachments)
    {
        long textBytes = GetTextBytes(text);
        long attachmentBytes = attachments.Sum(GetAttachmentBytes);
        if (textBytes > MaxTextBytesPerSession
            || attachments.Count > MaxAttachmentsPerSession
            || attachmentBytes > MaxAttachmentBytesPerSession)
        {
            return false;
        }

        bool alreadyStored = HasDraft(sessionId);
        bool hasContent = textBytes > 0 || attachments.Count > 0;
        if (hasContent && !alreadyStored && Count >= MaxRetainedSessions)
        {
            return false;
        }

        long retainedBytes = GetRetainedBytes() - GetSessionBytes(sessionId) + textBytes + attachmentBytes;
        return retainedBytes <= MaxRetainedBytes;
    }

    private long GetRetainedBytes() =>
        _drafts.Keys.Concat(_attachments.Keys).Distinct(StringComparer.Ordinal).Sum(GetSessionBytes);

    private long GetSessionBytes(string sessionId)
    {
        long textBytes = _drafts.TryGetValue(sessionId, out var text) ? GetTextBytes(text) : 0;
        long attachmentBytes = _attachments.TryGetValue(sessionId, out var attachments)
            ? attachments.Sum(GetAttachmentBytes)
            : 0;
        return textBytes + attachmentBytes;
    }

    private static long GetTextBytes(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);

    private static long GetAttachmentBytes(ImageAttachmentItem attachment) =>
        AttachmentMetadataAllowanceBytes
        + Encoding.UTF8.GetByteCount(attachment.FileName)
        + Encoding.UTF8.GetByteCount(attachment.FilePath ?? string.Empty)
        + Encoding.UTF8.GetByteCount(attachment.MimeType)
        + (attachment.Base64Data?.Length ?? 0)
        + GetTextBytes(attachment.TextContent);

    private static ImageAttachmentItem CloneAttachment(ImageAttachmentItem attachment)
    {
        var clone = new ImageAttachmentItem { IsImage = false };
        clone.Id = attachment.Id;
        clone.FileName = attachment.FileName;
        clone.FilePath = attachment.FilePath;
        clone.MimeType = attachment.MimeType;
        clone.ByteSize = attachment.ByteSize;
        clone.Base64Data = attachment.Base64Data;
        clone.TextContent = attachment.TextContent;
        clone.IsImage = attachment.IsImage;
        return clone;
    }

    private void SaveText(string sessionId, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _drafts.Remove(sessionId);
            return;
        }

        _drafts[sessionId] = text;
    }

    /// <summary>
    /// A session id has to be a real id; ChatView treats a missing or blank one as
    /// "no session yet", so keying a draft on it would strand the text.
    /// </summary>
    private static bool IsUsableSessionId(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId);
}
