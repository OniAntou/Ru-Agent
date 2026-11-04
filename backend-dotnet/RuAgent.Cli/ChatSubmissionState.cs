using RuAgent.Cli.Attachments;
using RuAgent.Cli.Images;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

/// <summary>
/// The draft's pending image/text attachments. Attachment loads finish on the
/// thread pool while the UI thread may submit at any moment, so every mutation
/// and snapshot takes the same gate; the exposed lists are copies, never live
/// views the caller could enumerate while another thread adds.
/// </summary>
internal sealed class ChatSubmissionState : IDisposable
{
    public const int MaxComposedPromptChars = AgentRunLimits.MaxPromptChars;
    private readonly object _gate = new();
    private readonly List<CliImageAttachment> _images = [];
    private readonly List<CliTextAttachment> _texts = [];
    private ChatSubmissionSnapshot? _retryable;

    public IReadOnlyList<CliImageAttachment> Images
    {
        get { lock (_gate) return _images.ToArray(); }
    }

    public IReadOnlyList<CliTextAttachment> Texts
    {
        get { lock (_gate) return _texts.ToArray(); }
    }

    public ChatSubmissionSnapshot? Retryable
    {
        get { lock (_gate) return _retryable; }
        private set { lock (_gate) _retryable = value; }
    }

    public int Count
    {
        get { lock (_gate) return _images.Count + _texts.Count; }
    }

    public int ImageCount
    {
        get { lock (_gate) return _images.Count; }
    }

    public long ImageBytes
    {
        get { lock (_gate) return _images.Sum(image => image.ByteSize); }
    }

    public long TextCharacters
    {
        get { lock (_gate) return _texts.Sum(text => (long)text.TextContent.Length); }
    }

    public long SourceBytes
    {
        get
        {
            lock (_gate)
            {
                return _images.Sum(image => image.ByteSize) + _texts.Sum(text => text.ByteSize);
            }
        }
    }

    public void Add(CliImageAttachment image)
    {
        lock (_gate) _images.Add(image);
    }

    /// <summary>
    /// Adds an image only when it keeps the count and total-byte caps, checked and
    /// applied under one lock so two concurrent loads cannot both pass the check.
    /// </summary>
    public bool TryAddImage(CliImageAttachment image, int maxCount, long maxTotalBytes, out long totalBytes)
    {
        lock (_gate)
        {
            totalBytes = _images.Sum(item => item.ByteSize) + image.ByteSize;
            if (_images.Count >= maxCount || totalBytes > maxTotalBytes)
            {
                return false;
            }

            _images.Add(image);
            return true;
        }
    }

    public void Add(CliTextAttachment text)
    {
        lock (_gate) _texts.Add(text);
    }

    public ChatSubmissionSnapshot? CreateSnapshot(
        string line, string sessionId, string workspace, string preset,
        string backend, string model, bool hasRetainedRun)
    {
        lock (_gate)
        {
            var isCommand = line.TrimStart().StartsWith('/');
            var isPresetCommand = line.TrimStart().Split(' ', 2)[0].Equals("/preset", StringComparison.OrdinalIgnoreCase);
            var isRetainedContinue = hasRetainedRun && string.Equals(line, "continue", StringComparison.OrdinalIgnoreCase);
            if (_images.Count + _texts.Count > 0 && isCommand
                && !ChatSubmissionSnapshot.IsPausedControlCommand(line) && !isPresetCommand)
            {
                return null;
            }

            var snapshot = ChatSubmissionSnapshot.Create(
                line, sessionId, workspace, preset, backend, model,
                isCommand || isRetainedContinue ? [] : _images,
                isCommand || isRetainedContinue ? [] : _texts);
            return AttachedPrompt.Compose(snapshot.Prompt, snapshot.Texts).Length > MaxComposedPromptChars
                ? null : snapshot;
        }
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
        var imageList = images.ToArray();
        var textList = texts.ToArray();
        lock (_gate)
        {
            foreach (var image in imageList) _images.Remove(image);
            foreach (var text in textList) _texts.Remove(text);
        }
    }

    public void Restore(IEnumerable<CliImageAttachment> images, IEnumerable<CliTextAttachment> texts)
    {
        var imageList = images.ToArray();
        var textList = texts.ToArray();
        lock (_gate)
        {
            _images.AddRange(imageList);
            _texts.AddRange(textList);
        }
    }

    public void HoldForRetry(ChatSubmissionSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        lock (_gate) _retryable ??= snapshot;
        Release(snapshot.Images);
    }

    public void Accept()
    {
        lock (_gate) _retryable = null;
    }

    public string? Detach(int oneBasedIndex)
    {
        CliImageAttachment? detachedImage = null;
        string? name = null;
        lock (_gate)
        {
            if (oneBasedIndex < 1 || oneBasedIndex > _images.Count + _texts.Count)
            {
                return null;
            }

            if (oneBasedIndex <= _images.Count)
            {
                detachedImage = _images[oneBasedIndex - 1];
                _images.RemoveAt(oneBasedIndex - 1);
                name = detachedImage.FileName;
            }
            else
            {
                var index = oneBasedIndex - _images.Count - 1;
                name = _texts[index].FileName;
                _texts.RemoveAt(index);
            }
        }

        if (detachedImage is not null)
        {
            Release([detachedImage]);
        }

        return name;
    }

    public void ClearPending()
    {
        CliImageAttachment[] images;
        lock (_gate)
        {
            images = _images.ToArray();
            _images.Clear();
            _texts.Clear();
        }

        Release(images);
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
        ChatSubmissionSnapshot? retry;
        lock (_gate)
        {
            retry = _retryable;
            _retryable = null;
        }

        if (retry is { } held)
        {
            Release(held.Images);
        }
    }
}
