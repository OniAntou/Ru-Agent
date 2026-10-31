using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>What the composer held when a send began.</summary>
public sealed record ComposerSnapshot(string RawText, IReadOnlyList<ImageAttachmentItem> Attachments)
{
    public string Text => RawText.Trim();

    public bool IsEmpty => Text.Length == 0 && Attachments.Count == 0;

    public int ImageCount => Attachments.Count(static attachment => attachment.IsImage);
}

/// <summary>
/// The composer's unsent content: attachments, the per-session draft, and the queue of
/// turns typed while a run is in hand.
/// </summary>
/// <remarks>
/// The text box stays the live source of the typed text. WinUI raises
/// <c>TextChanged</c> after the edit, so a copy held here could lag a keystroke made
/// while a send is awaiting the Host; every method that compares or saves text takes
/// the box's current value from the view. Text flows the other way only through
/// <see cref="TextReplaced"/>, raised on restore and clear, so typing never round-trips
/// through a binding and the caret and IME composition are left alone.
/// </remarks>
public sealed partial class ChatComposerViewModel : ObservableObject
{
    private readonly AgentRunPanelViewModel _runPanel;
    private readonly ComposerDraftStore _drafts;
    private bool _isRunInHand;

    public ChatComposerViewModel(
        AgentRunPanelViewModel runPanel,
        ComposerDraftStore? drafts = null,
        AgentTurnQueue? queue = null)
    {
        _runPanel = runPanel ?? throw new ArgumentNullException(nameof(runPanel));
        _drafts = drafts ?? new ComposerDraftStore();
        Queue = queue ?? new AgentTurnQueue();
        Attachments.CollectionChanged += (_, _) => HasAttachments = Attachments.Count > 0;
        Queue.Items.CollectionChanged += (_, _) => RefreshQueue();
        RefreshQueue();
    }

    /// <summary>The composer text must become this value (a restored draft, or empty after a send).</summary>
    internal event Action<string>? TextReplaced;

    public ObservableCollection<ImageAttachmentItem> Attachments { get; } = new();

    [ObservableProperty] public partial bool HasAttachments { get; private set; }

    public int ImageCount => Attachments.Count(static attachment => attachment.IsImage);

    public AgentTurnQueue Queue { get; }

    [ObservableProperty] public partial bool HasQueuedTurns { get; private set; }

    [ObservableProperty] public partial string QueueHeaderText { get; private set; } = string.Empty;

    /// <summary>
    /// While a run is in hand the queue drains on its own, so offering to send it by
    /// hand would promise a turn boundary the user cannot have yet.
    /// </summary>
    [ObservableProperty] public partial bool CanSendQueueNow { get; private set; }

    /// <summary>Whether a run is in hand, which decides whether the queue may be sent by hand.</summary>
    internal void SetRunInHand(bool isRunInHand)
    {
        _isRunInHand = isRunInHand;
        RefreshQueue();
    }

    private void RefreshQueue()
    {
        int count = Queue.Items.Count;
        HasQueuedTurns = count > 0;
        QueueHeaderText = count == 1 ? "1 message queued" : $"{count} messages queued";
        CanSendQueueNow = count > 0 && !_isRunInHand;
    }

    /// <summary>Typing: keeps the session's draft in step with the box.</summary>
    internal void EditDraft(string? sessionId, string text) =>
        ReportRetention(_drafts.TrySave(sessionId, text, Attachments));

    /// <summary>Puts a session's unsent text and attachments back in the composer.</summary>
    /// <remarks>The view prepares previews for the restored attachments; decoding them needs WinUI.</remarks>
    internal void RestoreDraft(string sessionId)
    {
        TextReplaced?.Invoke(_drafts.Restore(sessionId));
        Attachments.Clear();
        foreach (var attachment in _drafts.RestoreAttachments(sessionId))
        {
            Attachments.Add(attachment);
        }
    }

    /// <summary>
    /// Keeps the composer content for the session being left. False (with the limit
    /// shown) when it is over the retention bound, so the switch can be refused
    /// instead of silently dropping the draft.
    /// </summary>
    internal bool RetainDraft(string? sessionId, string text, bool checkOnly)
    {
        bool retained = checkOnly
            ? _drafts.CanSave(sessionId, text, Attachments.ToList())
            : _drafts.TrySave(sessionId, text, Attachments);
        if (!retained) _runPanel.ShowLocalStatus(ComposerDraftStore.RetentionLimitStatus);
        return retained;
    }

    internal void ClearDraft(string? sessionId) => _drafts.Clear(sessionId);

    /// <summary>Whether another image fits the turn; says so on the run panel when it does not.</summary>
    internal bool CanAcceptImage()
    {
        if (ChatAttachmentIntakePresenter.CanAddImage(ImageCount)) return true;
        _runPanel.ShowLocalStatus(ChatAttachmentPolicy.TooManyImagesStatus);
        return false;
    }

    /// <summary>
    /// Adds an attachment once it fits the draft bound. <paramref name="sessionId"/> is the
    /// session the intake started for; the caller has already checked it is still open.
    /// </summary>
    internal bool TryAddAttachment(string? sessionId, string text, ImageAttachmentItem attachment)
    {
        if (attachment.IsImage && !CanAcceptImage()) return false;

        var next = Attachments.Append(attachment).ToList();
        if (!_drafts.TrySave(sessionId, text, next))
        {
            _runPanel.ShowLocalStatus(ComposerDraftStore.RetentionLimitStatus);
            return false;
        }

        Attachments.Add(attachment);
        return true;
    }

    internal void RemoveAttachment(string? sessionId, string text, string attachmentId)
    {
        var attachment = Attachments.FirstOrDefault(item => item.Id == attachmentId);
        if (attachment is null) return;
        Attachments.Remove(attachment);
        ReportRetention(_drafts.TrySave(sessionId, text, Attachments));
    }

    internal ComposerSnapshot TakeSnapshot(string text) => new(text, Attachments.ToList());

    /// <summary>
    /// Whether the composer still holds exactly what was sent. A send that is accepted
    /// clears the composer only then, so text typed while it awaited the Host survives.
    /// </summary>
    internal bool IsUnchangedSince(ComposerSnapshot snapshot, string liveText) =>
        string.Equals(liveText, snapshot.RawText, StringComparison.Ordinal)
        && Attachments.SequenceEqual(snapshot.Attachments);

    /// <summary>The turn was accepted: the composer and the session's draft are emptied.</summary>
    internal void ClearAfterSend(string? sessionId)
    {
        TextReplaced?.Invoke(string.Empty);
        Attachments.Clear();
        _drafts.Clear(sessionId);
    }

    /// <summary>
    /// Clears after an accepted send and keeps what was sent until the run starts. The
    /// Host accepts a run before asking for workspace trust, so declining that prompt
    /// used to lose the message: it had already left the composer and the run never
    /// started (B7).
    /// </summary>
    internal void ClearAfterSend(string sessionId, ComposerSnapshot sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        ClearAfterSend(sessionId);
        _acceptedSends[sessionId] = sent;
    }

    /// <summary>The run for this session started; its message no longer needs keeping.</summary>
    internal void ForgetAcceptedSend(string? sessionId)
    {
        if (sessionId is not null) _acceptedSends.Remove(sessionId);
    }

    /// <summary>
    /// Puts a send whose run was declined back in the composer. Text typed since is
    /// never overwritten; returns whether the message came back.
    /// </summary>
    internal bool RestoreDeclinedSend(string sessionId, string liveText)
    {
        if (!_acceptedSends.Remove(sessionId, out var sent)) return false;
        if (!string.IsNullOrWhiteSpace(liveText) || Attachments.Count > 0) return false;

        TextReplaced?.Invoke(sent.RawText);
        foreach (var attachment in sent.Attachments) Attachments.Add(attachment);
        _drafts.TrySave(sessionId, sent.RawText, Attachments);
        return true;
    }

    private readonly Dictionary<string, ComposerSnapshot> _acceptedSends = new(StringComparer.Ordinal);

    /// <summary>Empties the text only, as when typed "Continue" resumes a run.</summary>
    internal void ClearText() => TextReplaced?.Invoke(string.Empty);

    /// <summary>
    /// Puts what the composer holds behind the run in hand. False means the content
    /// stayed where it was: nothing to queue, or the queue is full.
    /// </summary>
    internal bool QueueCurrent(string? sessionId, string text)
    {
        var snapshot = TakeSnapshot(text);
        if (snapshot.IsEmpty) return false;

        if (!Queue.TryEnqueue(snapshot.Text, snapshot.Text, snapshot.Attachments))
        {
            _runPanel.ShowLocalStatus(AgentComposerPresentation.QueueFullStatus);
            return false;
        }

        ClearAfterSend(sessionId);
        _runPanel.ShowLocalStatus(AgentComposerPresentation.QueuedStatus);
        return true;
    }

    /// <summary>
    /// The prompt the Agent actually receives: the typed text with every attached
    /// document inlined above it as labelled fenced blocks. The send path and the
    /// queue drain share it, so a turn is inlined the same way however it left
    /// the composer.
    /// </summary>
    public static string BuildEffectivePrompt(string text, IReadOnlyList<ImageAttachmentItem> attachments)
    {
        var textAttachments = attachments
            .Where(attachment => !attachment.IsImage && !string.IsNullOrWhiteSpace(attachment.TextContent))
            .ToList();
        if (textAttachments.Count == 0)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var document in textAttachments)
        {
            string language = document.FileExtension.TrimStart('.');
            builder.AppendLine($"[Attached file: {document.FileName} ({document.FormattedSize})]");
            builder.AppendLine($"```{language}");
            builder.AppendLine(document.TextContent);
            builder.AppendLine("```");
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            builder.AppendLine(text);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// The error shown when the composed prompt would exceed the Host's bound. The
    /// Host rejects the whole create call, so the GUI must refuse before sending
    /// rather than let a 5 MiB text attachment turn into a failed turn.
    /// </summary>
    public static string? PromptLimitStatus(string effectivePrompt)
    {
        var length = effectivePrompt.Length;
        return length <= AgentRunLimits.MaxPromptChars
            ? null
            : $"Prompt is {length.ToString("N0", CultureInfo.InvariantCulture)} / {AgentRunLimits.MaxPromptChars.ToString("N0", CultureInfo.InvariantCulture)} characters. Shorten the message or attach a smaller file.";
    }

    /// <summary>
    /// A draft over the bound says so; once it fits again that message is withdrawn.
    /// Any other composer message (a queue notice, a rejected file) is left alone.
    /// </summary>
    private void ReportRetention(bool saved)
    {
        if (!saved)
        {
            _runPanel.ShowLocalStatus(ComposerDraftStore.RetentionLimitStatus);
        }
        else if (_runPanel.LocalStatus == ComposerDraftStore.RetentionLimitStatus)
        {
            _runPanel.ClearLocalStatus();
        }
    }
}
