using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Subagents;

public sealed class SubagentPublicService(SqliteSubagentSnapshotReader snapshots,
    SubagentFamilyUsageReader usage, SqliteSubagentUserApplyStore actions,
    SqliteSubagentChangeSetStore changeSets, SubagentConcurrencyOptions concurrency,
    IAgentRuntimeRootRegistry? roots = null)
{
    public async Task<SubagentListResponse> ListAsync(RunId root, CancellationToken ct)
    {
        var snapshot = await snapshots.ReadAsync(root, ct).ConfigureAwait(false);
        var queued = await actions.ListAsync(root, ct).ConfigureAwait(false);
        var reported = await usage.ReadDetailedAsync(root, ct).ConfigureAwait(false);
        var totals = reported.Family;
        var rows = snapshot.Agents.Select(a => Map(a, queued, reported.Runs)).ToArray();
        var preparations = snapshot.Preparations.Select(a => new SubagentPreparationSummary(a.AgentId, a.Stage.ToString())).ToArray();
        var summary = new SubagentUsageSummary(totals.ModelSteps, totals.ReportedSteps, totals.InputTokens,
            totals.OutputTokens, totals.TotalTokens, totals.IsComplete);
        var running = rows.Count(row => OccupiesRunnableSlot(row.Attempts.LastOrDefault()?.Status));
        var status = AgentV1Shared.MapRunState(snapshot.Root.Status);
        var diagnostics = ProfileDiagnostics(root);
        // 0 on the wire means no admitted-child limit.
        var maxRunnable = concurrency.MaxRunnable ?? 0;
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { status, rows, preparations, snapshot.UnavailableAttempts, summary, MaxRunnable = maxRunnable, running, diagnostics })));
        return new(root.Value, status, fingerprint, rows, preparations, snapshot.UnavailableAttempts, summary,
            maxRunnable, running, diagnostics);
    }

    /// <summary>
    /// Why a profile was ignored, so a typo does not look like a missing feature. Only a
    /// live root has a registered workspace; otherwise the field is absent, not empty.
    /// </summary>
    private IReadOnlyList<string>? ProfileDiagnostics(RunId root)
    {
        if (roots is null || !roots.TryGetRoot(root, out var workspace) || string.IsNullOrWhiteSpace(workspace))
            return null;
        try { return SubagentProfileCatalog.Load(workspace).Diagnostics; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or PathTooLongException)
        { return ["profiles: unreadable"]; }
    }

    public async Task<SubagentDetailResponse> DetailAsync(RunId root, string agent, long? version, int offset, int limit, CancellationToken ct)
    {
        var list = await ListAsync(root, ct).ConfigureAwait(false);
        var row = list.Agents.SingleOrDefault(a => a.AgentId == agent) ?? throw new HarnessStoreException("subagent_not_found");
        SubagentResultPageResponse? page = null;
        if (version is { } value)
        {
            var result = await snapshots.ReadResultAsync(root, agent, value, offset, limit, ct).ConfigureAwait(false);
            page = new(value, result.Text, result.Offset, result.TotalBytes, result.NextOffset);
        }
        return new(root.Value, row, page);
    }

    /// <summary>
    /// Read-only change review for the coding result a parent may apply. It never
    /// writes, applies or approves anything; the base side is read from the
    /// parent workspace only when its current hash still matches the recorded
    /// base, so a diverged file is never presented as a clean diff.
    /// </summary>
    public async Task<SubagentChangesResponse> ChangesAsync(
        RunId root, string agentId, long version, CancellationToken ct)
    {
        var snapshot = await snapshots.ReadAsync(root, ct).ConfigureAwait(false);
        var agent = snapshot.Agents.SingleOrDefault(a => a.Agent.AgentId == agentId)
            ?? throw new HarnessStoreException("subagent_not_found");
        if (!string.Equals(agent.Agent.Role.ToString(), nameof(SubagentRole.Coding), StringComparison.OrdinalIgnoreCase))
            throw new HarnessStoreException("subagent_changes_not_found");
        var result = agent.Results.SingleOrDefault(r => r.Result.Version == version)
            ?? throw new HarnessStoreException("subagent_result_not_found");
        var changes = await changeSets.GetAsync(root, agentId, result.Result.RunId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_changes_not_found");
        if (changes.ResultVersion != version || changes.Files.Count > SubagentLimits.MaxSnapshotFiles)
            throw new HarnessStoreException("subagent_changes_invalid");

        var files = new List<SubagentChangedFileResponse>(changes.Files.Count);
        long totalChars = 0;
        foreach (var file in changes.Files)
        {
            ct.ThrowIfCancellationRequested();
            var current = await ReadCurrentFileAsync(changes.ParentRoot, file.RelativePath, ct).ConfigureAwait(false);
            string? currentHash = current.Bytes is null ? null : Hash(current.Bytes);
            bool exists = current.Exists;
            string status;
            if (file.ProposedHash == "missing")
            {
                status = !exists ? "applied" : currentHash == file.BaseHash ? "deleted" : "diverged";
            }
            else if (!exists)
            {
                status = file.BaseHash == "missing" ? "created" : "missing";
            }
            else if (currentHash == file.ProposedHash)
            {
                status = "applied";
            }
            else if (currentHash == file.BaseHash)
            {
                status = "base";
            }
            else
            {
                status = "diverged";
            }

            bool includeBase = status is "base" or "deleted";
            // A deletion proposes no content: null, not "", so a client cannot
            // mistake it for "replace with an empty file".
            bool deletion = file.ProposedHash == "missing";
            var proposed = deletion ? new ReviewText(null, false, false) : Text(file.Content.Span, ref totalChars);
            ReviewText? baseReview = includeBase && current.Bytes is { } baseBytes
                ? Text(baseBytes, ref totalChars)
                : null;
            bool binary = proposed.Binary || baseReview?.Binary == true;
            bool truncated = proposed.Truncated || baseReview?.Truncated == true
                || (includeBase && current.Bytes is null && !binary);
            files.Add(new SubagentChangedFileResponse(
                file.RelativePath, file.BaseHash, file.ProposedHash, file.Mode, status,
                binary, truncated, baseReview?.Text, proposed.Binary ? null : proposed.Text));
        }

        return new(root.Value, agentId, version, files);
    }

    private static bool OccupiesRunnableSlot(string? status) => status is not
        (null or "created" or "completed" or "failed" or "cancelled" or "interrupted");

    private const int MaxReviewTextChars = 64 * 1024;
    private const int MaxReviewTotalChars = 512 * 1024;
    private const long MaxReviewFileBytes = 8 * 1024 * 1024;

    private readonly record struct CurrentFile(bool Exists, byte[]? Bytes);
    private readonly record struct ReviewText(string? Text, bool Binary, bool Truncated);

    private static async Task<CurrentFile> ReadCurrentFileAsync(string parentRoot, string relativePath, CancellationToken ct)
    {
        if (!TryResolve(parentRoot, relativePath, out var full) || !File.Exists(full))
        {
            return new(false, null);
        }

        try
        {
            var info = new FileInfo(full);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaxReviewFileBytes)
            {
                return new(true, null);
            }

            return new(true, await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(true, null);
        }
    }

    private static bool TryResolve(string root, string relativePath, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 4096)
        {
            return false;
        }

        try
        {
            // Use the same canonical resolver as the apply path so a junction,
            // protected harness file or workspace escape cannot be read back as
            // a "diff".
            var resolved = new CanonicalPathResolver(root).Resolve(relativePath);
            if (!resolved.IsInsideWorkspace)
            {
                return false;
            }

            full = resolved.CanonicalPath;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException
            or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ReviewText Text(ReadOnlySpan<byte> bytes, ref long totalChars)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return new(null, true, false);
        }

        if (totalChars >= MaxReviewTotalChars)
        {
            return new(null, false, true);
        }

        int allowed = (int)Math.Min(MaxReviewTextChars, MaxReviewTotalChars - totalChars);
        bool truncated = text.Length > allowed;
        if (truncated)
        {
            text = text[..allowed];
        }

        totalChars += text.Length;
        return new(text, false, truncated);
    }

    private static SubagentUserActionSummary MapAction(SubagentUserApply a) => new(a.Status, a.Revision, a.ApprovalId, a.Code);
    private static SubagentSummary Map(SubagentSnapshotEntry entry, IReadOnlyList<SubagentUserApply> actions, IReadOnlyDictionary<string, SubagentFamilyUsage> usage) => new(
        entry.Agent.AgentId, entry.Agent.Role.ToString().ToLowerInvariant(), entry.Task, entry.Agent.Canceled,
        Profile: entry.Profile, Attempts: entry.Attempts.Select(a => new SubagentAttemptSummary(a.Attempt.Run.RunId.Value, a.Attempt.AttemptNumber,
            AgentV1Shared.MapRunState(a.Attempt.Run.Status), a.Attempt.Run.Version,
            a.Model?.ProviderId, a.Model?.Model, a.Model?.Protocol, a.Attempt.DeadlineAt,
            a.Approval is { } approval ? new(approval.ApprovalId.Value, approval.ToolId, approval.Arguments, approval.ArgumentsTruncated) : null,
            usage.TryGetValue(a.Attempt.Run.RunId.Value, out var reported)
                ? new(reported.ModelSteps, reported.ReportedSteps, reported.InputTokens, reported.OutputTokens, reported.TotalTokens, reported.IsComplete)
                : new(0, 0, null, null, null, false),
            a.Attempt.CreatedAt, a.Attempt.AdmittedAt,
            a.Attempt.Run.State.IsTerminal() ? a.Attempt.Run.TerminalAt ?? a.Attempt.Run.UpdatedAt : null)).ToArray(),
        Results: entry.Results.Select(r => new SubagentResultSummary(r.Result.Version, r.Result.RunId.Value,
            AgentV1Shared.MapRunState(new(r.Result.State)), r.Result.Code, r.Apply?.Status.ToString().ToLowerInvariant(),
            r.Apply?.AppliedFiles, r.Acknowledged,
            actions.SingleOrDefault(a => a.AgentId == entry.Agent.AgentId && a.Version == r.Result.Version) is { } action ? MapAction(action) : null)).ToArray(),
        Detached: entry.Agent.Detached);
}
