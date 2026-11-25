namespace RuAgent.Broker.Tools.Browser;

/// <summary>The answer an engine sends for one JavaScript dialog.</summary>
internal sealed record DialogAnswer(bool Accept, string? PromptText);

/// <summary>
/// One page's JavaScript dialog answers (ADR 0055, amended 2026-11-25). A dialog blocks the page
/// until it is answered, so the engine answers each one as it opens: with the one-shot answer the
/// Agent set through <c>action=dialog</c>, or by default accepting alert and beforeunload and
/// dismissing confirm and prompt. Answered dialogs are kept, bounded, for the next tool result.
/// </summary>
internal sealed class BrowserDialogBook
{
    private const int MaxKept = 20;
    private readonly object _gate = new();
    private readonly List<BrowserDialog> _answered = [];
    private DialogAnswer? _next;

    public void SetNextAnswer(bool accept, string? promptText)
    {
        lock (_gate) _next = new DialogAnswer(accept, accept ? promptText : null);
    }

    public DialogAnswer Answer(string kind, string message)
    {
        var normalized = kind is "alert" or "confirm" or "prompt" or "beforeunload" ? kind : "alert";
        DialogAnswer answer;
        lock (_gate)
        {
            answer = _next ?? new DialogAnswer(normalized is "alert" or "beforeunload", null);
            _next = null;
            if (normalized != "prompt") answer = answer with { PromptText = null };
            if (_answered.Count >= MaxKept) _answered.RemoveAt(0);
            var bounded = message.Length <= RuAgent.Protocol.BrowserLimits.MaxDialogMessageChars
                ? message
                : message[..RuAgent.Protocol.BrowserLimits.MaxDialogMessageChars];
            _answered.Add(new BrowserDialog(normalized, bounded, answer.Accept));
        }

        return answer;
    }

    public IReadOnlyList<BrowserDialog> Take()
    {
        lock (_gate)
        {
            if (_answered.Count == 0) return [];
            var taken = _answered.ToArray();
            _answered.Clear();
            return taken;
        }
    }
}
