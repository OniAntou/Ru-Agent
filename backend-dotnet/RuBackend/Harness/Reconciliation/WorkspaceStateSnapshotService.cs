using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Broker.Tools.Git;
using RuAgent.Harness.Core;
using RuBackend.Harness.Git;

namespace RuBackend.Harness.Reconciliation;

/// <summary>
/// Computes the fresh workspace/Git snapshot hash shown with a reconciliation request.
/// When this host holds the run's workspace root, the hash is a live Git fingerprint of
/// HEAD, branch, and status; otherwise it falls back to a deterministic hash over the
/// durable start state recorded by Task 6 so a restarted host can still detect staleness
/// fail-closed. Raw diffs and file contents never enter any hash input here — only
/// hashes, codes, and paths already stored in the durable record.
/// </summary>
public sealed class WorkspaceStateSnapshotService
{
    private const int MaxGitOutputBytes = 1 * 1024 * 1024;
    private static readonly IGitCli Git = new StreamingGitCli(
        outputLimitBytes: MaxGitOutputBytes,
        timeoutMs: 10_000);
    private readonly SqliteRunGitStateStore _gitStateStore;
    private readonly Func<RunId, string?>? _rootResolver;

    public WorkspaceStateSnapshotService(
        SqliteRunGitStateStore gitStateStore,
        Func<RunId, string?>? rootResolver = null)
    {
        _gitStateStore = gitStateStore ?? throw new ArgumentNullException(nameof(gitStateStore));
        _rootResolver = rootResolver;
    }

    public async Task<string?> ComputeFreshHashAsync(RunId runId, CancellationToken cancellationToken = default)
    {
        if (_rootResolver?.Invoke(runId) is { } root)
        {
            var fingerprint = await TryGitFingerprintAsync(root, cancellationToken).ConfigureAwait(false);
            if (fingerprint is not null)
            {
                return fingerprint;
            }
        }

        return await ComputeDurableStateHashAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ComputeDurableStateHashAsync(
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _gitStateStore.TryGetStartSnapshotAsync(runId.Value, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string>? paths = await _gitStateStore.TryGetAttributedPathsAsync(runId.Value, cancellationToken).ConfigureAwait(false);
        if (snapshot is null && paths is null)
        {
            return null;
        }

        var canonical = JsonSerializer.Serialize(new
        {
            snapshot?.RepositoryRoot,
            snapshot?.HeadOid,
            snapshot?.BranchName,
            snapshot?.IsDetached,
            snapshot?.IndexHash,
            snapshot?.HasStagedChanges,
            snapshot?.ManagedCommitEligible,
            SnapshotEntries = snapshot is null ? null : SerializeEntries(snapshot),
            ObservedFileHashes = snapshot?.ObservedFileHashes,
            AttributedPaths = paths,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static object? SerializeEntries(RuAgent.Protocol.Harness.RunGitWorktreeSnapshot snapshot) =>
        snapshot.Entries.Select(entry => new
        {
            entry.IndexCode,
            entry.WorktreeCode,
            Kind = entry.Kind.ToString(),
            entry.IsSubmodule,
            entry.Path,
            entry.OriginalPath,
            entry.RenameScore,
        }).ToArray();

    private static async Task<string?> TryGitFingerprintAsync(string root, CancellationToken cancellationToken)
    {
        var head = await Git.RunAsync(["rev-parse", "HEAD"], root, cancellationToken).ConfigureAwait(false);
        if (head.ExitCode != 0)
        {
            return null;
        }

        var branchResult = await Git.RunAsync(
            ["symbolic-ref", "--quiet", "--short", "HEAD"], root, cancellationToken).ConfigureAwait(false);
        var branch = branchResult.ExitCode == 0 ? branchResult.Stdout : "(detached)";
        var status = await Git.RunAsync(["status", "--porcelain=v2"], root, cancellationToken).ConfigureAwait(false);
        if (status.ExitCode != 0)
        {
            return null;
        }

        var payload = $"{head.Stdout}\n{branch}\n{status.Stdout}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
