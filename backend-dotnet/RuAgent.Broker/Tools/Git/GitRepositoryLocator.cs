namespace RuAgent.Broker.Tools.Git;

public enum GitRepositoryResolution
{
    Found,
    NoRepository,
    Failed,
}

public sealed record GitRepositoryLocation(
    GitRepositoryResolution Resolution,
    string? RepositoryRoot,
    string StderrTail);

/// <summary>
/// Resolves the containing repository for a working directory through one fixed
/// read-only Git invocation. A failed invocation resolves to <see cref="GitRepositoryResolution.Failed"/>;
/// absence of a repository is only concluded from the explicit Git diagnostic.
/// </summary>
public sealed class GitRepositoryLocator(IGitCli cli)
{
    public async Task<GitRepositoryLocation> LocateAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return new GitRepositoryLocation(GitRepositoryResolution.Failed, null, "empty_working_directory");
        }

        var result = await cli.RunAsync(
            new[] { "rev-parse", "--show-toplevel" },
            workingDirectory,
            cancellationToken);

        if (result.Succeeded)
        {
            var root = result.Stdout.Trim();
            if (root.Length == 0)
            {
                return new GitRepositoryLocation(GitRepositoryResolution.Failed, null, "empty_repository_root");
            }

            return new GitRepositoryLocation(
                GitRepositoryResolution.Found,
                Path.GetFullPath(root.TrimEnd('/', '\\')),
                string.Empty);
        }

        if (result.Stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
        {
            return new GitRepositoryLocation(GitRepositoryResolution.NoRepository, null, result.Stderr);
        }

        return new GitRepositoryLocation(GitRepositoryResolution.Failed, null, string.IsNullOrEmpty(result.FailureReason) ? result.Stderr : result.FailureReason);
    }
}
