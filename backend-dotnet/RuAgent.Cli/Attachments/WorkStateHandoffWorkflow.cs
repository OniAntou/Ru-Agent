using System.Text;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Attachments;

public sealed class WorkStateHandoffWorkflow
{
    private PendingPreview? _pending;

    public sealed record Outcome(string? Preview, string? Error)
    {
        public bool IsReady => Preview is not null && Error is null;
    }

    private sealed record PendingPreview(string TargetSessionId, string Content);

    public async Task<Outcome> PreviewAsync(
        string selectedRunId,
        string targetSessionId,
        Func<string, CancellationToken, Task<GetAgentRunResponse>> getRunAsync,
        Func<bool> isTargetCurrent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(getRunAsync);
        ArgumentNullException.ThrowIfNull(isTargetCurrent);
        _pending = null;
        if (!isTargetCurrent())
        {
            return new(null, "handoff_target_changed: preview again for the current session");
        }
        try
        {
            var run = await getRunAsync(selectedRunId, cancellationToken).ConfigureAwait(false);
            if (!isTargetCurrent())
            {
                return new(null, "handoff_target_changed: preview again for the current session");
            }
            if (!string.Equals(run.RunId, selectedRunId, StringComparison.Ordinal))
            {
                return new(null, "handoff_source_mismatch: selected run identity did not match");
            }

            var content = WorkStateHandoffContent.Create(run);
            if (content is null)
            {
                return new(null, "handoff_unavailable: selected run has no bounded safe work-state preview");
            }

            _pending = new PendingPreview(targetSessionId, content);
            return new(content, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(null, "handoff_cancelled: selected run was not read");
        }
        catch (Exception)
        {
            return new(null, "handoff_unavailable: selected run could not be read");
        }
    }

    public bool TryAttach(
        string targetSessionId,
        IList<CliTextAttachment> pendingTexts,
        int totalPendingAttachmentCount,
        long totalPendingAttachmentBytes,
        int maximumAttachments)
    {
        ArgumentNullException.ThrowIfNull(pendingTexts);
        if (_pending is not { } pending
            || !string.Equals(pending.TargetSessionId, targetSessionId, StringComparison.Ordinal)
            || totalPendingAttachmentCount < 0
            || totalPendingAttachmentBytes < 0
            || totalPendingAttachmentCount >= maximumAttachments)
        {
            return false;
        }

        var byteSize = Encoding.UTF8.GetByteCount(_pending.Content);
        if (totalPendingAttachmentBytes > ChatProjectFileWorkflow.MaxPendingAttachmentBytes - byteSize)
        {
            return false;
        }

        pendingTexts.Add(new CliTextAttachment(
            "saved-work-context.txt",
            pending.Content,
            byteSize));
        _pending = null;
        return true;
    }

    public void Clear() => _pending = null;
}
