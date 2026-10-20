using RuAgent.Cli.Attachments;
using RuAgent.Cli.Images;

namespace RuAgent.Cli;

internal sealed class ChatSubmissionState : IDisposable
{
    public const int MaxComposedPromptChars = 16_000;
    private readonly List<CliImageAttachment> _images = [];
    private readonly List<CliTextAttachment> _texts = [];

    public ChatSubmissionState()
    {
        Images = _images.AsReadOnly();
        Texts = _texts.AsReadOnly();
    }

    public IReadOnlyList<CliImageAttachment> Images { get; }
    public IReadOnlyList<CliTextAttachment> Texts { get; }
    public ChatSubmissionSnapshot? Retryable { get; private set; }
    public int Count => _images.Count + _texts.Count;
    public long SourceBytes => checked(_images.Sum(image => image.ByteSize) + _texts.Sum(text => text.ByteSize));

    public void Add(CliImageAttachment image) => _images.Add(image);
    public void Add(CliTextAttachment text) => _texts.Add(text);

    public ChatSubmissionSnapshot? CreateSnapshot(
        string line, string sessionId, string workspace, string preset,
        string backend, string model, bool isParked)
    {
        var isCommand = line.TrimStart().StartsWith('/');
        var isParkedContinue = isParked && string.Equals(line, "continue", StringComparison.OrdinalIgnoreCase);
        if (Count > 0 && isCommand && !ChatSubmissionSnapshot.IsPausedControlCommand(line))
        {
            return null;
        }

        var snapshot = ChatSubmissionSnapshot.Create(
            line, sessionId, workspace, preset, backend, model,
            isCommand || isParkedContinue ? [] : _images,
            isCommand || isParkedContinue ? [] : _texts);
        return AttachedPrompt.Compose(snapshot.Prompt, snapshot.Texts).Length > MaxComposedPromptChars
            ? null : snapshot;
    }

    public void Admit(ChatSubmissionSnapshot snapshot) => RemovePending(snapshot.Images, snapshot.Texts);

    public Transfer BeginTransfer(
        IEnumerable<CliImageAttachment> images, IEnumerable<CliTextAttachment> texts,
        bool shouldClaimPending)
    {
        var transfer = new Transfer(this, images.ToArray(), texts.ToArray());
        if (shouldClaimPending)
        {
            RemovePending(transfer.Images, transfer.Texts);
        }

        return transfer;
    }

    internal sealed class Transfer(
        ChatSubmissionState owner, CliImageAttachment[] images, CliTextAttachment[] texts) : IDisposable
    {
        private bool _isReleased;
        public IReadOnlyList<CliImageAttachment> Images { get; } = Array.AsReadOnly(images);
        public IReadOnlyList<CliTextAttachment> Texts { get; } = Array.AsReadOnly(texts);

        public void ReturnToPending()
        {
            if (_isReleased)
            {
                return;
            }

            owner.Restore(Images, Texts);
            _isReleased = true;
        }

        public void Dispose()
        {
            if (_isReleased)
            {
                return;
            }

            Release(Images);
            _isReleased = true;
        }
    }

    public void RemovePending(IEnumerable<CliImageAttachment> images, IEnumerable<CliTextAttachment> texts)
    {
        foreach (var image in images.ToArray())
        {
            _images.Remove(image);
        }

        foreach (var text in texts.ToArray())
        {
            _texts.Remove(text);
        }
    }

    public void Restore(IEnumerable<CliImageAttachment> images, IEnumerable<CliTextAttachment> texts)
    {
        _images.AddRange(images);
        _texts.AddRange(texts);
    }

    public void HoldForRetry(ChatSubmissionSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        Retryable ??= snapshot;
        Release(snapshot.Images);
    }

    public void Accept() => Retryable = null;

    public string? Detach(int oneBasedIndex)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > Count)
        {
            return null;
        }

        if (oneBasedIndex <= _images.Count)
        {
            var image = _images[oneBasedIndex - 1];
            _images.RemoveAt(oneBasedIndex - 1);
            Release([image]);
            return image.FileName;
        }

        var index = oneBasedIndex - _images.Count - 1;
        var text = _texts[index];
        _texts.RemoveAt(index);
        return text.FileName;
    }

    public void ClearPending()
    {
        Release(_images);
        _images.Clear();
        _texts.Clear();
    }

    public static void Release(IEnumerable<CliImageAttachment> images)
    {
        foreach (var image in images)
        {
            if (image.TempPath is not { } path)
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
        ClearPending();
        if (Retryable is { } retry)
        {
            Release(retry.Images);
            Retryable = null;
        }
    }
}
