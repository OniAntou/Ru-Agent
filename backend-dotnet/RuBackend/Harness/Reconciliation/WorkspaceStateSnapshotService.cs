using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
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
        var head = await RunGitAsync(root, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
        if (head is null)
        {
            return null;
        }

        var branch = await RunGitAsync(root, cancellationToken, "symbolic-ref", "--quiet", "--short", "HEAD").ConfigureAwait(false)
                     ?? "(detached)";
        var status = await RunGitAsync(root, cancellationToken, "status", "--porcelain=v2").ConfigureAwait(false);
        if (status is null)
        {
            return null;
        }

        var payload = $"{head}\n{branch}\n{status}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static async Task<string?> RunGitAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = StrictUtf8,
                StandardErrorEncoding = StrictUtf8,
            };
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            // Same hardening as the Broker's git paths: this runs against a
            // user-supplied workspace automatically, so configuration that can
            // select a program (core.fsmonitor) is pinned off before any
            // subcommand, and optional index locks stay off.
            startInfo.ArgumentList.Add("--no-optional-locks");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("core.fsmonitor=false");
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxGitOutputBytes, cancellationToken);
            var stderrTask = ReadBoundedAsync(process.StandardError, MaxGitOutputBytes, cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                TryKill(process);
                return null;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (
            error is InvalidOperationException or System.ComponentModel.Win32Exception
                or IOException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadBoundedAsync(
        StreamReader reader,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var chars = new char[4096];
        var bytes = 0;
        var exceeded = false;
        while (true)
        {
            var read = await reader.ReadAsync(chars.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var chunkBytes = StrictUtf8.GetByteCount(chars.AsSpan(0, read));
            if (bytes <= maxBytes - chunkBytes)
            {
                builder.Append(chars, 0, read);
                bytes += chunkBytes;
            }
            else
            {
                exceeded = true;
            }
        }

        return exceeded ? null : builder.ToString();
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
