using System.Text;
using Microsoft.Extensions.Logging;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuBackend.Business.Sessions;

namespace RuBackend.Harness;

/// <summary>
/// Images a model step sends: the run's own attachments and earlier conversation images.
/// <see cref="OmittedCurrentRun"/> counts the run's own older images left out so the request
/// stays within its image bound.
/// </summary>
public sealed record RunImageSet(IReadOnlyList<string> CurrentRun, IReadOnlyList<string> Earlier, int OmittedCurrentRun = 0);

/// <summary>
/// Re-attaches recent earlier conversation images to a run so a follow-up turn can still
/// see what the user sent before. Each run previously loaded only its own UserImage
/// payloads, so a text follow-up lost the image even though the conversation kept its
/// receipt fact.
/// </summary>
/// <remarks>
/// Bounded on purpose: at most <see cref="MaxImagesPerRequest"/> images per request (the same request bound as a
/// normal turn), at most 20 MiB decoded in total, only for a run whose pinned model
/// accepts image input, and only images whose user message is still visible in the
/// session (rewind, pruning and fork prefixes are respected). The current run's own
/// attachments keep their existing order and are never displaced by earlier ones.
/// A failure to read an earlier image never fails the run: earlier images are optional
/// reference, while the current turn's attachments stay required.
/// </remarks>
public sealed class SessionImageCarryover
{
    public const int MaxImagesPerRequest = RuAgent.Protocol.Harness.AgentRunLimits.MaxImages;
    public const long MaxTotalImageBytes = RuAgent.Protocol.Harness.AgentRunLimits.MaxTotalImageBytes;

    private readonly HarnessPayloadStore _payloads;
    private readonly IHarnessEventStore _runs;
    private readonly ISessionStore _sessions;
    private readonly Func<RunId, CancellationToken, Task<bool>> _visionPinned;
    private readonly ILogger<SessionImageCarryover>? _logger;

    public SessionImageCarryover(
        HarnessPayloadStore payloads,
        IHarnessEventStore runs,
        ISessionStore sessions,
        Func<RunId, CancellationToken, Task<bool>> visionPinned,
        ILogger<SessionImageCarryover>? logger = null)
    {
        _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _visionPinned = visionPinned ?? throw new ArgumentNullException(nameof(visionPinned));
        _logger = logger;
    }

    public async Task<RunImageSet> LoadAsync(RunId runId, CancellationToken cancellationToken)
    {
        var (current, omitted) = NewestWithinBound(
            await ReadRunImagesAsync(runId, cancellationToken).ConfigureAwait(false));
        if (current.Count >= MaxImagesPerRequest || omitted > 0)
        {
            return new RunImageSet(current, Array.Empty<string>(), omitted);
        }

        try
        {
            var earlier = await ReadEarlierImagesAsync(runId, current, cancellationToken).ConfigureAwait(false);
            return new RunImageSet(current, earlier);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Earlier images are reference material; losing them must not fail a run whose
            // own turn is readable. The model still receives the session's receipt facts.
            _logger?.LogWarning(error, "earlier session image carry-over skipped");
            return new RunImageSet(current, Array.Empty<string>());
        }
    }

    /// <summary>
    /// The run's own images that one request carries. A run's first message and each steer
    /// may bring their own images, so a run can hold more than one request may carry: the
    /// newest are kept, in their order, within the count and decoded-byte bounds, and the
    /// rest are counted instead of failing the step.
    /// </summary>
    internal static (IReadOnlyList<string> Kept, int Omitted) NewestWithinBound(
        IReadOnlyList<string> images, int maxImages = MaxImagesPerRequest)
    {
        var kept = new List<string>();
        long totalBytes = 0;
        for (var index = images.Count - 1; index >= 0 && kept.Count < maxImages; index--)
        {
            var decoded = DecodedBytes(images[index]);
            if (totalBytes + decoded > MaxTotalImageBytes) break;
            kept.Add(images[index]);
            totalBytes += decoded;
        }

        kept.Reverse();
        return (kept, images.Count - kept.Count);
    }

    /// <summary>The prompt note saying older images of this run are no longer attached.</summary>
    public static string AppendOmittedImageNotice(string prompt, int omittedCount, int perRequest = MaxImagesPerRequest)
    {
        if (omittedCount <= 0) return prompt;
        var noun = omittedCount == 1 ? "image" : "images";
        var note = $"[{omittedCount} older {noun} of this run are no longer attached; only the most recent "
            + $"{perRequest} images are sent with each request.]";
        return string.IsNullOrWhiteSpace(prompt) ? note : note + "\n\n" + prompt;
    }

    /// <summary>
    /// The prompt note that tells the model the attached images came from earlier turns
    /// rather than from the current message. Null/empty prompts keep their shape.
    /// </summary>
    public static string AppendEarlierImageNotice(string prompt, int earlierImageCount)
    {
        if (earlierImageCount <= 0) return prompt;
        var noun = earlierImageCount == 1 ? "image" : "images";
        var note = $"[Earlier conversation images re-attached for reference: {earlierImageCount} {noun}. "
            + "They were sent in earlier user turns of this session and are not attachments of the "
            + "current message; use them only as visual context for those earlier turns.]";
        return string.IsNullOrWhiteSpace(prompt) ? note : note + "\n\n" + prompt;
    }

    private async Task<IReadOnlyList<string>> ReadRunImagesAsync(RunId runId, CancellationToken cancellationToken)
    {
        // Reads through the payload store so the audience rule and the encrypted-payload
        // contract stay in one place instead of being re-implemented as raw SQL.
        var payloadIds = await _payloads
            .ListPayloadIdsAsync(runId, HarnessPayloadKind.UserImage, cancellationToken)
            .ConfigureAwait(false);
        var list = new List<string>(payloadIds.Count);
        foreach (var payloadId in payloadIds)
        {
            var content = await _payloads.ReadForModelAsync(runId, payloadId, cancellationToken)
                .ConfigureAwait(false);
            list.Add(Encoding.UTF8.GetString(content.Plaintext.Span));
        }

        return list;
    }

    private async Task<IReadOnlyList<string>> ReadEarlierImagesAsync(
        RunId runId,
        IReadOnlyList<string> current,
        CancellationToken cancellationToken)
    {
        if (!await _visionPinned(runId, cancellationToken).ConfigureAwait(false))
        {
            // A text-only pin already receives an explicit unavailable-image notice for its
            // own turn; injecting older images would only change that notice's meaning.
            return Array.Empty<string>();
        }

        var run = await _runs.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Array.Empty<string>();
        }

        var session = await _sessions.GetAsync(run.SessionId.Value, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Array.Empty<string>();
        }

        long totalBytes = 0;
        foreach (var image in current)
        {
            totalBytes += DecodedBytes(image);
        }

        var earlier = new List<string>();
        // Newest user turn first, so a follow-up sees the most recent image the user sent.
        foreach (var message in session.Messages.Reverse())
        {
            if (current.Count + earlier.Count >= MaxImagesPerRequest) break;
            if (!string.Equals(message.Role, "user", StringComparison.Ordinal)
                || message.ImageAttachmentCount <= 0
                || message.SourceRunId is not { } sourceRun)
            {
                continue;
            }

            if (string.Equals(sourceRun, runId.Value, StringComparison.Ordinal))
            {
                continue;
            }

            var sourceRunId = new RunId(sourceRun);
            var payloadIds = await _payloads
                .ListPayloadIdsAsync(sourceRunId, HarnessPayloadKind.UserImage, cancellationToken)
                .ConfigureAwait(false);
            foreach (var payloadId in payloadIds)
            {
                if (current.Count + earlier.Count >= MaxImagesPerRequest) break;
                var content = await _payloads.ReadForModelAsync(sourceRunId, payloadId, cancellationToken)
                    .ConfigureAwait(false);
                var dataUrl = Encoding.UTF8.GetString(content.Plaintext.Span);
                var decoded = DecodedBytes(dataUrl);
                if (decoded <= 0 || decoded > ModelImageMaximumDecodedBytes)
                {
                    continue;
                }

                if (totalBytes + decoded > MaxTotalImageBytes)
                {
                    return earlier;
                }

                earlier.Add(dataUrl);
                totalBytes += decoded;
            }
        }

        return earlier;
    }

    // The adapter-level per-image bound; RuAgent.Protocol is not a RuBackend dependency,
    // so the value is pinned here next to the total request bound it feeds.
    private const long ModelImageMaximumDecodedBytes = 4L * 1024 * 1024;

    private static long DecodedBytes(string dataUrl)
    {
        var marker = dataUrl.IndexOf(";base64,", StringComparison.Ordinal);
        if (marker < 0) return 0;
        var base64Length = dataUrl.Length - marker - 8;
        if (base64Length <= 0) return 0;
        var padding = dataUrl[^1] == '=' ? 1 : 0;
        if (dataUrl.Length > 1 && dataUrl[^2] == '=') padding++;
        return Math.Max(0, (base64Length * 3L / 4) - padding);
    }
}
